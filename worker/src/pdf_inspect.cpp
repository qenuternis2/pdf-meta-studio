#include "pdf_doc.hpp"
#include "object_fields.hpp"

#include "sha256.hpp"
#include "xmp_model.hpp"
#include "logical_graph.hpp"
#include "private_data.hpp"

#include <qpdf/Buffer.hh>
#include <qpdf/Pipeline.hh>
#include <qpdf/QPDFAcroFormDocumentHelper.hh>
#include <qpdf/QPDFAnnotationObjectHelper.hh>
#include <qpdf/QPDFEFStreamObjectHelper.hh>
#include <qpdf/QPDFEmbeddedFileDocumentHelper.hh>
#include <qpdf/QPDFExc.hh>
#include <qpdf/QPDFFileSpecObjectHelper.hh>
#include <qpdf/QPDFFormFieldObjectHelper.hh>
#include <qpdf/QPDFOutlineDocumentHelper.hh>
#include <qpdf/QPDFPageDocumentHelper.hh>
#include <qpdf/QPDFPageObjectHelper.hh>

#include <unordered_map>
#include <unordered_set>

namespace pm {

std::string refOf(QPDFObjGen og) {
    return std::to_string(og.getObj()) + " " + std::to_string(og.getGen());
}

QPDFObjGen parseRef(const std::string& ref) {
    auto sp = ref.find(' ');
    if (sp == std::string::npos) throw WorkerError("bad_request", "Некорректная ссылка на объект: " + ref);
    return QPDFObjGen(std::stoi(ref.substr(0, sp)), std::stoi(ref.substr(sp + 1)));
}

LoadedPdf openPdf(const fs::path& path, const std::string& password, Context* ctx) {
    LoadedPdf pdf;
    pdf.path = path;
    pdf.q = std::make_unique<QPDF>();
    pdf.q->setSuppressWarnings(true);
    if (ctx) ctx->progress("open", 0);
    std::string p8 = pathToUtf8(path);
    try {
        pdf.q->processFile(p8.c_str(), password.empty() ? nullptr : password.c_str());
    } catch (const QPDFExc& e) {
        if (e.getErrorCode() == qpdf_e_password)
            throw WorkerError(password.empty() ? "password_required" : "password_incorrect",
                              password.empty() ? "Документ защищён паролем" : "Неверный пароль");
        if (e.getErrorCode() == qpdf_e_system)
            throw WorkerError("file_locked", "Не удалось прочитать файл: " + sanitizeUtf8(e.getMessageDetail()));
        if (e.getErrorCode() == qpdf_e_unsupported)
            throw WorkerError("unsupported", "Неподдерживаемая возможность PDF: " + sanitizeUtf8(e.getMessageDetail()));
        throw WorkerError("pdf_damaged", "PDF повреждён или не распознан: " + sanitizeUtf8(e.getMessageDetail()));
    } catch (const std::exception& e) {
        std::string msg = e.what();
        if (msg.find("unsupported") != std::string::npos || msg.find("security handler") != std::string::npos)
            throw WorkerError("unsupported_encryption", "Неподдерживаемый способ защиты PDF: " + sanitizeUtf8(msg));
        throw WorkerError("pdf_open_failed", "Не удалось открыть PDF: " + sanitizeUtf8(msg));
    }
    for (auto& w : pdf.q->getWarnings()) pdf.warnings.push_back(sanitizeUtf8(w.what()));
    return pdf;
}

json MetaOwner::toJson() const {
    json j{{"ref", refOf(og)}, {"kind", kind}, {"label", label}};
    if (!keyPath.empty()) j["keyPath"] = keyPath;
    j["path"] = path;
    if (pageIndex >= 0) j["page"] = pageIndex + 1;
    return j;
}

MetaStream* Discovery::find(QPDFObjGen og) {
    for (auto& s : streams)
        if (s.og == og) return &s;
    return nullptr;
}

namespace {

// Приёмник распакованных данных с ограничением размера: маленький сжатый поток не должен
// разворачиваться в гигабайты памяти («zip-бомба»).
class CappedSink : public Pipeline {
public:
    explicit CappedSink(size_t cap) : Pipeline("pdfmeta capped sink", nullptr), cap_(cap) {}
    void write(unsigned char const* data, size_t len) override {
        if (overflow_) return;
        if (len > cap_ - out.size()) {
            overflow_ = true;
            throw std::length_error("stream data exceeds limit");
        }
        out.append(reinterpret_cast<const char*>(data), len);
    }
    void finish() override {}
    bool overflow() const { return overflow_; }
    std::string out;
private:
    size_t cap_;
    bool overflow_ = false;
};

}  // namespace

std::string streamBytes(QPDFObjectHandle stream) {
    CappedSink sink(kMaxXmpPacketBytes + 1);
    bool attempted = false;
    bool ok = false;
    try {
        ok = stream.pipeStreamData(&sink, &attempted, 0, qpdf_dl_all, true);
    } catch (const std::length_error&) {
        if (!sink.overflow()) throw;
    }
    if (sink.overflow())
        throw WorkerError("xmp_too_large", "Поток метаданных после распаковки превышает " +
                                               std::to_string(kMaxXmpPacketBytes >> 20) + " МиБ");
    if (!ok || !attempted) throw std::runtime_error("Не удалось распаковать данные потока " + refOf(stream.getObjGen()));
    return std::move(sink.out);
}

static std::string nameOr(QPDFObjectHandle d, const std::string& key) {
    if (!d.isDictionary()) return "";
    auto v = d.getKey(key);
    return v.isName() ? v.getName() : "";
}

static std::string classify(QPDFObjectHandle obj, QPDFObjGen rootOg,
                            const std::unordered_map<std::string, int>& pageIndex, int& page) {
    QPDFObjectHandle dict = obj.isStream() ? obj.getDict() : obj;
    page = -1;
    if (obj.isIndirect()) {
        if (obj.getObjGen() == rootOg) return "catalog";
        auto it = pageIndex.find(refOf(obj.getObjGen()));
        if (it != pageIndex.end()) {
            page = it->second;
            return "page";
        }
    }
    std::string type = nameOr(dict, "/Type");
    std::string subtype = nameOr(dict, "/Subtype");
    if (obj.isStream() && subtype == "/Image") return "image";
    if (obj.isStream() && subtype == "/Form") return "form";
    if (type == "/EmbeddedFile") return "embeddedFile";
    if (type == "/Filespec" || dict.hasKey("/EF")) return "filespec";
    if (type == "/Annot" || (dict.hasKey("/Rect") && !subtype.empty())) return "annotation";
    if (type == "/FontDescriptor") return "font";
    if (type == "/Pages") return "pages";
    if (type == "/OutputIntent") return "outputIntent";
    return "object";
}

static std::string labelFor(const std::string& kind, QPDFObjGen og, int page, QPDFObjectHandle obj) {
    std::string r = refOf(og) + " R";
    if (kind == "catalog") return "Документ (каталог)";
    if (kind == "page") return "Страница " + std::to_string(page + 1);
    if (kind == "image") return "Изображение " + r;
    if (kind == "form") return "Form XObject " + r;
    if (kind == "annotation") {
        QPDFObjectHandle d = obj.isStream() ? obj.getDict() : obj;
        std::string st = nameOr(d, "/Subtype");
        return "Аннотация " + (st.empty() ? std::string() : st.substr(1) + " ") + r;
    }
    if (kind == "filespec") return "Вложение " + r;
    if (kind == "embeddedFile") return "Поток вложения " + r;
    if (kind == "font") return "Шрифт " + r;
    return "Объект " + r;
}

Discovery discover(QPDF& q, Context* ctx) {
    Discovery d;
    QPDFObjGen rootOg = q.getRoot().getObjGen();
    std::unordered_map<std::string, int> pageIndex;
    try {
        auto pages = q.getAllPages();
        for (size_t i = 0; i < pages.size(); ++i) pageIndex[refOf(pages[i].getObjGen())] = static_cast<int>(i);
    } catch (const std::exception& e) {
        d.issues.push_back({"pages", sanitizeUtf8(e.what())});
    }

    struct Item {
        QPDFObjectHandle obj;
        QPDFObjGen owner;
        std::string keyPath;
        json path = json::array();
        size_t depth = 0;
    };
    std::vector<Item> stack;
    std::unordered_set<std::string> visited;
    stack.push_back({q.getTrailer(), QPDFObjGen(), ""});
    std::unordered_map<std::string, size_t> streamIndex;
    size_t steps = 0;

    auto recordOwner = [&](QPDFObjectHandle holder, QPDFObjGen ownerOg, const std::string& keyPath, const json& path,
                           QPDFObjectHandle meta) {
        if (!meta.isStream() || !meta.isIndirect()) {
            d.issues.push_back({"metadata", "Ключ /Metadata в " + refOf(ownerOg) + " не указывает на поток"});
            return;
        }
        std::string sref = refOf(meta.getObjGen());
        auto it = streamIndex.find(sref);
        if (it == streamIndex.end()) {
            MetaStream ms;
            ms.stream = meta;
            ms.og = meta.getObjGen();
            d.streams.push_back(ms);
            it = streamIndex.emplace(sref, d.streams.size() - 1).first;
        }
        int page = -1;
        MetaOwner o;
        o.og = ownerOg;
        o.keyPath = keyPath;
        o.path = path;
        o.kind = classify(holder, rootOg, pageIndex, page);
        o.pageIndex = page;
        o.label = labelFor(o.kind, ownerOg, page, holder);
        if (!keyPath.empty()) o.label += " · " + keyPath;
        d.streams[it->second].owners.push_back(o);
    };

    auto walk = [&] {
        while (!stack.empty()) {
            Item it = stack.back();
            stack.pop_back();
            if (++steps % 2000 == 0 && ctx) ctx->checkCancel();
            if (steps > 1000000 || it.depth > 128) {
                d.issues.push_back({"limits", "Превышен лимит обхода: 1 000 000 элементов или 128 уровней"});
                if (steps > 1000000) { stack.clear(); break; }
                continue;
            }
            try {
                QPDFObjectHandle obj = it.obj;
                QPDFObjGen owner = it.owner;
                std::string keyPath = it.keyPath;
                json path = it.path;
                if (obj.isIndirect()) {
                    std::string r = refOf(obj.getObjGen());
                    if (!visited.insert(r).second) continue;
                    owner = obj.getObjGen();
                    keyPath.clear();
                    path = json::array();
                }
                if (obj.isArray()) {
                    int n = obj.getArrayNItems();
                    if (n > 100000 || stack.size() + n > 100000)
                        throw WorkerError("scan_limit", "Очередь обхода превышает 100 000 элементов");
                    for (int i = n - 1; i >= 0; --i) {
                        json child = path;
                        child.push_back(i);
                        stack.push_back({obj.getArrayItem(i), owner, keyPath + "[" + std::to_string(i) + "]", child, it.depth + 1});
                    }
                    continue;
                }
                if (!obj.isDictionary() && !obj.isStream()) continue;
                QPDFObjectHandle dict = obj.isStream() ? obj.getDict() : obj;
                // Standalone streams must explicitly identify themselves as PDF metadata.
                // A generic XML stream is not an XMP container.
                if (obj.isStream() && nameOr(dict, "/Type") == "/Metadata" &&
                    nameOr(dict, "/Subtype") == "/XML") {
                    std::string r = refOf(obj.getObjGen());
                    if (!streamIndex.count(r)) {
                        MetaStream ms;
                        ms.stream = obj;
                        ms.og = obj.getObjGen();
                        streamIndex[r] = d.streams.size();
                        d.streams.push_back(ms);
                    }
                }
                if (owner.isIndirect() && dict.hasKey("/Metadata"))
                    recordOwner(obj, owner, keyPath, path, dict.getKey("/Metadata"));
                if (owner.isIndirect() && dict.hasKey("/PieceInfo")) {
                    QPDFObjectHandle pi = dict.getKey("/PieceInfo");
                    json entry{{"owner", refOf(owner)}, {"keyPath", keyPath}, {"path", path}, {"apps", json::array()}};
                    if (pi.isDictionary()) {
                        for (const auto& app : pi.getKeys()) {
                            QPDFObjectHandle a = pi.getKey(app);
                            json aj{{"name", sanitizeUtf8(app)}};
                            if (a.isDictionary()) {
                                aj["address"] = privateAddress(refOf(owner), path, app);
                                aj["adapter"] = supportedPrivateEntry(a) ? json("PdfMetaStudioV1") : json(nullptr);
                                if (supportedPrivateEntry(a)) aj["fields"] = privateFields(a);
                                aj["diagnostic"] = pdfValueTree(a.getKey("/Private"));
                                if (a.getKey("/LastModified").isString())
                                    aj["lastModified"] = sanitizeUtf8(a.getKey("/LastModified").getUTF8Value());
                                QPDFObjectHandle priv = a.getKey("/Private");
                                aj["private"] = priv.isNull() ? "none" : priv.isStream() ? "stream" : priv.isDictionary() ? "dictionary" : "other";
                            }
                            entry["apps"].push_back(aj);
                        }
                    }
                    d.pieceInfo.push_back(entry);
                }
                std::string base = keyPath.empty() ? "" : keyPath;
                for (const auto& key : dict.getKeys()) {
                    if (stack.size() >= 100000) throw WorkerError("scan_limit", "Очередь обхода превышает 100 000 элементов");
                    QPDFObjectHandle v = dict.getKey(key);
                    if (v.isIndirect() || v.isArray() || v.isDictionary() || v.isStream()) {
                        json child = path;
                        child.push_back(key);
                        stack.push_back({v, owner, base + key, child, it.depth + 1});
                    }
                }
            } catch (const Cancelled&) {
                throw;
            } catch (const std::exception& e) {
                d.issues.push_back({"object " + refOf(it.owner), sanitizeUtf8(e.what())});
            }
        }
    };
    walk();
    const auto reachable = visited;
    d.reachableObjects = reachable.size();

    // Объекты текущей таблицы ссылок, в том числе без владельцев.
    try {
        auto all = q.getAllObjects();
        d.totalObjects = all.size();
        for (auto& obj : all) {
            if (steps > 1000000) break;
            if (++steps % 2000 == 0 && ctx) ctx->checkCancel();
            try {
                // Traverse unreferenced dictionaries and their direct children too.
                // They can own Metadata or PieceInfo even without a catalog link.
                stack.push_back({obj, obj.getObjGen(), ""});
                walk();
            } catch (const Cancelled&) {
                throw;
            } catch (const std::exception& e) {
                d.issues.push_back({"xref " + refOf(obj.getObjGen()), sanitizeUtf8(e.what())});
            }
        }
    } catch (const Cancelled&) {
        throw;
    } catch (const std::exception& e) {
        d.issues.push_back({"xref", sanitizeUtf8(e.what())});
    }
    for (auto& ms : d.streams) ms.reachable = reachable.count(refOf(ms.og)) > 0;
    return d;
}

static json stringValueJson(QPDFObjectHandle v) {
    json j;
    if (v.isString()) {
        std::string raw = v.getStringValue();
        j["kind"] = "string";
        j["value"] = sanitizeUtf8(v.getUTF8Value());
        if (raw.size() >= 2 && static_cast<unsigned char>(raw[0]) == 0xFE && static_cast<unsigned char>(raw[1]) == 0xFF)
            j["encoding"] = "utf16be";
        else if (raw.size() >= 3 && raw.compare(0, 3, "\xEF\xBB\xBF") == 0)
            j["encoding"] = "utf8";
        else
            j["encoding"] = "pdfdoc";
    } else if (v.isName()) {
        j["kind"] = "name";
        j["value"] = sanitizeUtf8(v.getName());
    } else if (v.isBool()) {
        j["kind"] = "bool";
        j["value"] = v.getBoolValue() ? "true" : "false";
    } else if (v.isNumber()) {
        j["kind"] = "number";
        j["value"] = v.unparse();
    } else {
        j["kind"] = "other";
        j["value"] = sanitizeUtf8(v.unparse());
    }
    return j;
}

json infoToJson(QPDF& q) {
    QPDFObjectHandle trailer = q.getTrailer();
    json j{{"present", false}, {"entries", json::array()}};
    if (!trailer.hasKey("/Info")) return j;
    QPDFObjectHandle info = trailer.getKey("/Info");
    if (!info.isDictionary()) {
        j["error"] = "/Info не является словарём";
        return j;
    }
    j["present"] = true;
    if (info.isIndirect()) j["ref"] = refOf(info.getObjGen());
    for (const auto& key : info.getKeys()) {
        QPDFObjectHandle v = info.getKey(key);
        if (v.isNull()) continue;  // null в словаре = отсутствие ключа
        json e = stringValueJson(v);
        e["diagnostic"] = pdfValueTree(v);
        e["key"] = sanitizeUtf8(key);
        j["entries"].push_back(e);
    }
    return j;
}

json encryptionInfo(QPDF& q) {
    json j{{"encrypted", q.isEncrypted()}};
    if (!q.isEncrypted()) return j;
    int R = 0, P = 0, V = 0;
    QPDF::encryption_method_e sm, stm, fm;
    q.isEncrypted(R, P, V, sm, stm, fm);
    auto mname = [](QPDF::encryption_method_e m) -> std::string {
        switch (m) {
        case QPDF::e_none: return "none";
        case QPDF::e_rc4: return "RC4";
        case QPDF::e_aes: return "AES-128";
        case QPDF::e_aesv3: return "AES-256";
        default: return "unknown";
        }
    };
    j["R"] = R;
    j["V"] = V;
    j["P"] = P;
    j["streamMethod"] = mname(sm);
    j["ownerPasswordMatched"] = q.ownerPasswordMatched();
    j["userPasswordMatched"] = q.userPasswordMatched();
    j["allowModifyOther"] = q.allowModifyOther();
    j["allowModifyAnnotation"] = q.allowModifyAnnotation();
    return j;
}

json signatureInfo(QPDF& q) {
    json j{{"signed", false}, {"signatureFields", 0}, {"usageRights", false}};
    try {
        QPDFAcroFormDocumentHelper af(q);
        int count = 0;
        if (af.hasAcroForm()) {
            for (auto& f : af.getFormFields())
                if (f.getFieldType() == "/Sig" && f.getValue().isDictionary()) ++count;
            QPDFObjectHandle sf = q.getRoot().getKey("/AcroForm").getKey("/SigFlags");
            if (sf.isInteger()) j["sigFlags"] = sf.getIntValue();
        }
        j["signatureFields"] = count;
        QPDFObjectHandle perms = q.getRoot().getKey("/Perms");
        j["usageRights"] = perms.isDictionary();
        j["signed"] = count > 0 || perms.isDictionary();
    } catch (const std::exception& e) {
        j["error"] = sanitizeUtf8(e.what());
    }
    return j;
}

static std::string hashStreamData(QPDFObjectHandle s) {
    auto buf = s.getRawStreamData();  // после расшифровки, до фильтров
    Sha256 h;
    h.update(buf->getBuffer(), buf->getSize());
    return h.hexDigest();
}

static int countOutlines(std::vector<QPDFOutlineObjectHelper> items, int depth) {
    if (depth > 64) return 0;
    int n = 0;
    for (auto& o : items) n += 1 + countOutlines(o.getKids(), depth + 1);
    return n;
}

json structureSnapshot(QPDF& q) {
    json j;
    auto pages = q.getAllPages();
    j["pageCount"] = pages.size();
    json contents = json::array();
    json annots = json::array();
    for (auto& p : pages) {
        QPDFPageObjectHelper ph(p);
        Sha256 h;
        for (auto& c : ph.getPageContents()) {
            std::string d = hashStreamData(c);
            h.update(d.data(), d.size());
        }
        contents.push_back(h.hexDigest());
        annots.push_back(ph.getAnnotations().size());
    }
    j["pageContents"] = contents;
    j["annotationsPerPage"] = annots;
    j["annotationContents"] = annotationContentsHashes(q);
    QPDFOutlineDocumentHelper od(q);
    j["outlines"] = countOutlines(od.getTopLevelOutlines(), 0);
    QPDFAcroFormDocumentHelper af(q);
    j["formFields"] = af.hasAcroForm() ? af.getFormFields().size() : 0;
    json att = json::object();
    QPDFEmbeddedFileDocumentHelper ef(q);
    for (auto& [name, spec] : ef.getEmbeddedFiles()) {
        QPDFObjectHandle s = spec->getEmbeddedFileStream();
        att[sanitizeUtf8(name)] = s.isStream() ? hashStreamData(s) : "";
    }
    j["attachments"] = att;
    return j;
}

static json pagesJson(QPDF& q) {
    json arr = json::array();
    auto pages = q.getAllPages();
    for (size_t i = 0; i < pages.size() && i < 5000; ++i) {
        QPDFPageObjectHelper ph(pages[i]);
        json p{{"index", i + 1}};
        try {
            QPDFObjectHandle mb = ph.getMediaBox();
            if (mb.isRectangle()) {
                auto r = mb.getArrayAsRectangle();
                p["width"] = r.urx - r.llx;
                p["height"] = r.ury - r.lly;
            }
            QPDFObjectHandle rot = pages[i].getKey("/Rotate");
            if (rot.isInteger()) p["rotate"] = rot.getIntValue();
        } catch (const std::exception&) {
        }
        arr.push_back(p);
    }
    return arr;
}

static json annotationsJson(QPDF& q) {
    json arr = json::array();
    auto pages = q.getAllPages();
    for (size_t i = 0; i < pages.size(); ++i) {
        int index = 0;
        for (auto& a : QPDFPageObjectHelper(pages[i]).getAnnotations()) {
            ++index;
            QPDFObjectHandle o = a.getObjectHandle();
            json j{{"page", i + 1}, {"subtype", sanitizeUtf8(a.getSubtype())}};
            if (o.isIndirect()) j["ref"] = refOf(o.getObjGen());
            else j["ref"] = "page:" + std::to_string(i + 1) + ":annot:" + std::to_string(index);
            for (const char* k : {"/T", "/Subj", "/M", "/CreationDate", "/NM"}) {
                QPDFObjectHandle v = o.getKey(k);
                if (v.isString()) j[std::string(k).substr(1)] = sanitizeUtf8(v.getUTF8Value());
            }
            j["hasContents"] = o.hasKey("/Contents");
            // Direct annotations use their stable page and annotation position within the opened snapshot.
            j["editable"] = o.isDictionary();
            // Значения редактируемых полей: null — ключа нет в документе.
            json fields = json::object();
            for (const char* f : {"author", "subject", "modified", "created"}) {
                QPDFObjectHandle v = o.getKey(findObjectField("annotation", f)->key);
                fields[f] = v.isString() ? json(sanitizeUtf8(v.getUTF8Value())) : json(nullptr);
            }
            j["fields"] = fields;
            j["hasMetadata"] = o.hasKey("/Metadata");
            arr.push_back(j);
        }
    }
    return arr;
}

static json attachmentsJson(QPDF& q) {
    json arr = json::array();
    QPDFEmbeddedFileDocumentHelper ef(q);
    for (auto& [name, spec] : ef.getEmbeddedFiles()) {
        json j{{"name", sanitizeUtf8(name)}, {"filename", sanitizeUtf8(spec->getFilename())},
               {"description", sanitizeUtf8(spec->getDescription())}};
        QPDFObjectHandle so = spec->getObjectHandle();
        if (so.isIndirect()) j["ref"] = refOf(so.getObjGen());
        QPDFObjectHandle s = spec->getEmbeddedFileStream();
        if (s.isStream()) {
            QPDFEFStreamObjectHelper eh(s);
            j["creationDate"] = sanitizeUtf8(eh.getCreationDate());
            j["modDate"] = sanitizeUtf8(eh.getModDate());
            j["size"] = eh.getSize();
            j["subtype"] = sanitizeUtf8(eh.getSubtype());
        }
        // Значения редактируемых полей: null — ключа нет в документе.
        json fields = json::object();
        for (const char* f : {"filename", "description", "created", "modified"})
            fields[f] = readObjectField(q, "attachment", name, *findObjectField("attachment", f));
        j["fields"] = fields;
        arr.push_back(j);
    }
    return arr;
}

static json profilesJson(QPDF& q, XmpDoc* doc) {
    json j = json::array();
    if (doc) {
        std::string a, b;
        if (doc->getSimple("http://www.aiim.org/pdfa/ns/id/", "part", a)) {
            doc->getSimple("http://www.aiim.org/pdfa/ns/id/", "conformance", b);
            j.push_back("PDF/A-" + a + b);
        }
        if (doc->getSimple("http://www.aiim.org/pdfua/ns/id/", "part", a)) j.push_back("PDF/UA-" + a);
        if (doc->getSimple("http://www.npes.org/pdfx/ns/id/", "GTS_PDFXVersion", a)) j.push_back(a);
    }
    QPDFObjectHandle info = q.getTrailer().getKey("/Info");
    if (info.isDictionary() && info.getKey("/GTS_PDFXVersion").isString()) {
        std::string v = info.getKey("/GTS_PDFXVersion").getUTF8Value();
        if (std::find(j.begin(), j.end(), v) == j.end()) j.push_back(sanitizeUtf8(v));
    }
    return j;
}

json inspect(LoadedPdf& pdf, Context& ctx) {
    QPDF& q = *pdf.q;
    json out;
    ctx.progress("inspect", 10);
    out["pdf"] = json{{"version", q.getPDFVersion()}, {"extensionLevel", q.getExtensionLevel()}};
    std::vector<ScanIssue> issues;
    try {
        out["pdf"]["pageCount"] = q.getAllPages().size();
        out["pages"] = pagesJson(q);
        if (q.getAllPages().size() > 5000) issues.push_back({"pages", "Only the first 5000 page dimensions are included"});
    } catch (const std::exception& e) {
        issues.push_back({"pages", sanitizeUtf8(e.what())});
    }
    out["encryption"] = encryptionInfo(q);
    out["signatures"] = signatureInfo(q);
    out["info"] = infoToJson(q);

    ctx.progress("inspect", 30);
    Discovery d = discover(q, &ctx);
    issues.insert(issues.end(), d.issues.begin(), d.issues.end());

    ctx.progress("inspect", 60);
    json streams = json::array();
    std::unique_ptr<XmpDoc> catalogDoc;
    // Общий объём пакетов в ответе: тысячи потоков по десятки МиБ иначе раздувают ответ GUI.
    size_t packetBudget = kMaxSnapshotPacketBytes;
    QPDFObjectHandle rootMeta = q.getRoot().getKey("/Metadata");
    for (auto& ms : d.streams) {
        ctx.checkCancel();
        json s{{"ref", refOf(ms.og)}, {"reachable", ms.reachable}, {"owners", json::array()}};
        for (auto& o : ms.owners) s["owners"].push_back(o.toJson());
        bool isDocument = rootMeta.isStream() && rootMeta.getObjGen() == ms.og;
        s["document"] = isDocument;
        try {
            QPDFObjectHandle filter = ms.stream.getDict().getKey("/Filter");
            if (!filter.isNull()) s["filter"] = filter.unparse();
            std::string packet = streamBytes(ms.stream);
            if (packet.size() > packetBudget)
                throw WorkerError("xmp_too_large", "Суммарный объём XMP-потоков документа превышает " +
                                                       std::to_string(kMaxSnapshotPacketBytes >> 20) + " МиБ");
            packetBudget -= packet.size();
            s["length"] = packet.size();
            s["sha256"] = Sha256::hex(packet);
            if (isValidUtf8(packet))
                s["packet"] = packet;
            else
                s["packetBase64"] = toBase64(packet);
            try {
                auto doc = XmpDoc::parse(packet);
                s["model"] = doc->model();
                s["parse"] = json{{"ok", true}};
                if (isDocument) catalogDoc = std::move(doc);
            } catch (const WorkerError& e) {
                s["parse"] = json{{"ok", false}, {"code", e.code}, {"error", sanitizeUtf8(e.what())}};
                issues.push_back({"metadata " + refOf(ms.og), sanitizeUtf8(e.what())});
                // Для просмотра — модель из нестрогого разбора; правка такого пакета заблокирована.
                if (e.code == "xmp_invalid") {
                    try {
                        s["lenientModel"] = XmpDoc::parse(packet, false)->model();
                    } catch (const WorkerError&) {
                    }
                }
            }
        } catch (const Cancelled&) {
            throw;
        } catch (const WorkerError& e) {
            s["parse"] = json{{"ok", false}, {"code", e.code}, {"error", sanitizeUtf8(e.what())}};
            issues.push_back({"metadata " + refOf(ms.og), sanitizeUtf8(e.what())});
        } catch (const std::exception& e) {
            s["parse"] = json{{"ok", false}, {"code", "stream_unreadable"}, {"error", sanitizeUtf8(e.what())}};
            issues.push_back({"metadata " + refOf(ms.og), sanitizeUtf8(e.what())});
        }
        streams.push_back(s);
    }
    out["metadataStreams"] = streams;
    out["profiles"] = profilesJson(q, catalogDoc.get());

    ctx.progress("inspect", 85);
    try {
        out["annotations"] = annotationsJson(q);
    } catch (const std::exception& e) {
        issues.push_back({"annotations", sanitizeUtf8(e.what())});
    }
    try {
        out["attachments"] = attachmentsJson(q);
    } catch (const std::exception& e) {
        issues.push_back({"attachments", sanitizeUtf8(e.what())});
    }
    out["pieceInfo"] = d.pieceInfo;
    out["objects"] = json{{"total", d.totalObjects}, {"reachable", d.reachableObjects}};

    json scan{{"complete", issues.empty()}, {"issues", json::array()}};
    for (auto& i : issues) scan["issues"].push_back(json{{"area", i.area}, {"reason", i.reason}});
    out["scan"] = scan;
    out["warnings"] = pdf.warnings;
    for (auto& w : q.getWarnings()) out["warnings"].push_back(sanitizeUtf8(w.what()));
    if (!out["warnings"].empty()) {
        out["scan"]["complete"] = false;
        out["scan"]["issues"].push_back(json{{"area", "pdf_structure"}, {"reason", "qpdf reported a malformed or repaired structure; writing is blocked until it can be read without repairs"}});
    }
    ctx.progress("inspect", 100);
    return out;
}

}  // namespace pm
