#include "pdf_edit.hpp"

#include "fileutil.hpp"
#include "object_fields.hpp"
#include "pdf_doc.hpp"
#include "sha256.hpp"
#include "xmp_model.hpp"

#include <qpdf/QPDFWriter.hh>
#include <qpdf/QPDFSystemError.hh>
#include <cerrno>

#include <chrono>
#include <ctime>

namespace pm {

namespace {

struct StreamChange {
    std::string originalRef;  // пусто для нового потока
    std::string action;       // edit | create | detach | remove
    QPDFObjectHandle stream;  // поток после правки (для remove — исходный)
    std::vector<MetaOwner> owners;  // затронутые владельцы
    std::string beforePacket, afterPacket;
    json beforeModel, afterModel;
};

struct ObjectChange {
    std::string kind;     // annotation | attachment
    std::string address;  // аннотация: ссылка "N G"; вложение: ключ в /EmbeddedFiles
    std::string label;
    const ObjectField* field = nullptr;
    QPDFObjGen og;        // словарь аннотации (для поиска после перенумерации при записи)
    json before, after;   // null — ключа нет
};

struct Applied {
    json infoBefore, infoAfter;
    bool infoChanged = false;
    std::vector<StreamChange> streams;
    std::vector<ObjectChange> objects;
    bool needsPdf14 = false;
    std::vector<std::string> notes;
};

std::string getPassword(const json& req) { return req.value("password", std::string()); }

fs::path requirePath(const json& req, const char* key) {
    if (!req.contains(key) || !req[key].is_string() || req[key].get<std::string>().empty())
        throw WorkerError("bad_request", std::string("Не указан параметр ") + key);
    return pathFromUtf8(req[key].get<std::string>());
}

QPDFObjectHandle resolveOwner(QPDF& q, const std::string& ref) {
    if (ref == "catalog") return q.getRoot();
    QPDFObjectHandle o = q.getObject(parseRef(ref));
    if (o.isNull()) throw WorkerError("bad_request", "Объект-владелец не найден: " + ref);
    return o;
}

QPDFObjectHandle ownerDict(QPDFObjectHandle o) { return o.isStream() ? o.getDict() : o; }

QPDFObjectHandle metadataOwner(QPDF& q, const MetaOwner& owner) {
    QPDFObjectHandle obj = ownerDict(q.getObject(owner.og));
    for (const auto& step : owner.path) {
        if (step.is_string()) obj = ownerDict(obj).getKey(step.get<std::string>());
        else if (step.is_number_integer() && obj.isArray()) obj = obj.getArrayItem(step.get<int>());
        else throw WorkerError("bad_request", "Некорректный путь владельца метаданных");
    }
    obj = ownerDict(obj);
    if (!obj.isDictionary()) throw WorkerError("bad_request", "Владелец метаданных не является словарём");
    return obj;
}

QPDFObjectHandle newMetadataStream(QPDF& q, const std::string& packet) {
    QPDFObjectHandle s = q.newStream(packet);
    s.getDict().replaceKey("/Type", QPDFObjectHandle::newName("/Metadata"));
    s.getDict().replaceKey("/Subtype", QPDFObjectHandle::newName("/XML"));
    return s;
}

void applyInfoOps(QPDF& q, const json& ops, Applied& a) {
    a.infoBefore = infoToJson(q);
    if (!ops.is_array() || ops.empty()) {
        a.infoAfter = a.infoBefore;
        return;
    }
    QPDFObjectHandle trailer = q.getTrailer();
    QPDFObjectHandle info = trailer.getKey("/Info");
    if (!info.isDictionary()) {
        info = q.makeIndirectObject(QPDFObjectHandle::newDictionary());
        trailer.replaceKey("/Info", info);
        a.notes.push_back("Создан словарь /Info");
    }
    for (const auto& op : ops) {
        std::string kind = op.at("op").get<std::string>();
        std::string key = op.at("key").get<std::string>();
        if (key.size() < 2 || key[0] != '/') throw WorkerError("bad_request", "Ключ /Info должен начинаться с /: " + key);
        if (kind == "set") {
            std::string type = op.value("type", "string");
            std::string value = op.at("value").get<std::string>();
            if (type == "string") {
                info.replaceKey(key, QPDFObjectHandle::newUnicodeString(value));
            } else if (type == "name") {
                if (value.empty() || value[0] != '/') value = "/" + value;
                info.replaceKey(key, QPDFObjectHandle::newName(value));
            } else {
                throw WorkerError("bad_request", "Неподдерживаемый тип значения /Info: " + type);
            }
        } else if (kind == "delete") {
            info.removeKey(key);
        } else {
            throw WorkerError("bad_request", "Неизвестная операция /Info: " + kind);
        }
    }
    a.infoAfter = infoToJson(q);
    json b = a.infoBefore, c = a.infoAfter;
    b.erase("ref");
    c.erase("ref");
    a.infoChanged = b != c;
}

void applyXmpTargets(QPDF& q, Discovery& d, const json& targets, Applied& a) {
    if (!targets.is_array()) return;
    for (const auto& t : targets) {
        std::string action = t.value("action", "edit");
        const json ops = t.value("ops", json::array());
        StreamChange ch;
        if (!t.contains("stream") || t["stream"].is_null()) {
            // Новый поток метаданных для владельца.
            std::string ownerRef = t.value("owner", "catalog");
            QPDFObjectHandle owner = resolveOwner(q, ownerRef);
            if (ownerDict(owner).hasKey("/Metadata"))
                throw WorkerError("bad_request", "У объекта уже есть поток /Metadata: " + ownerRef);
            auto doc = XmpDoc::empty();
            ch.beforeModel = doc->model();
            doc->apply(ops);
            ch.afterPacket = doc->serialize();
            ch.afterModel = doc->model();
            ch.stream = newMetadataStream(q, ch.afterPacket);
            ownerDict(owner).replaceKey("/Metadata", ch.stream);
            ch.action = "create";
            MetaOwner mo;
            mo.og = owner.getObjGen();
            mo.kind = ownerRef == "catalog" ? "catalog" : "object";
            mo.label = ownerRef == "catalog" ? "Документ (каталог)" : "Объект " + ownerRef + " R";
            ch.owners.push_back(mo);
            a.needsPdf14 = true;
            a.streams.push_back(std::move(ch));
            continue;
        }

        std::string sref = t["stream"].get<std::string>();
        MetaStream* ms = d.find(parseRef(sref));
        if (!ms) throw WorkerError("bad_request", "Поток метаданных не найден: " + sref);
        ch.originalRef = sref;
        std::string scope = t.value("scope", "");
        if (ms->owners.size() > 1 && scope != "all" && scope != "detach")
            throw WorkerError("scope_required",
                              "Поток " + sref + " используют несколько владельцев: выберите правку для всех или отделение копии",
                              json{{"stream", sref}, {"owners", ms->owners.size()}});

        std::vector<MetaOwner> affected;
        if (scope == "detach") {
            std::string ownerRef = t.at("owner").get<std::string>();
            json ownerPath = t.value("ownerPath", json::array());
            for (auto& o : ms->owners)
                if ((refOf(o.og) == ownerRef || (ownerRef == "catalog" && o.kind == "catalog")) &&
                    o.path == ownerPath) affected.push_back(o);
            if (affected.empty()) throw WorkerError("bad_request", "Владелец " + ownerRef + " не использует поток " + sref);
            affected.resize(1);
        } else {
            affected = ms->owners;
        }
        ch.owners = affected;
        bool replaces = !ops.empty() && ops[0].value("op", "") == "replacePacket";
        try {
            ch.beforePacket = streamBytes(ms->stream);
        } catch (const WorkerError& e) {
            // Слишком большой пакет нельзя разобрать, но можно заменить целиком или удалить.
            if (e.code != "xmp_too_large" || !(replaces || action == "remove")) throw;
        }

        if (action == "remove") {
            for (auto& o : affected) {
                metadataOwner(q, o).removeKey("/Metadata");
            }
            ch.action = "remove";
            ch.stream = ms->stream;
            try {
                ch.beforeModel = XmpDoc::parse(ch.beforePacket)->model();
            } catch (const WorkerError&) {
            }
            a.streams.push_back(std::move(ch));
            continue;
        }

        std::unique_ptr<XmpDoc> doc;
        try {
            doc = XmpDoc::parse(ch.beforePacket);
            ch.beforeModel = doc->model();
        } catch (const WorkerError& e) {
            if (!replaces)
                throw WorkerError("xmp_source_invalid",
                                  "Исходный XMP повреждён: изменить его можно только заменой всего пакета. " +
                                      std::string(e.what()));
            doc = XmpDoc::empty();
        }
        doc->apply(ops);
        ch.afterPacket = doc->serialize();
        ch.afterModel = doc->model();
        if (scope == "detach") {
            ch.stream = newMetadataStream(q, ch.afterPacket);
            metadataOwner(q, affected[0]).replaceKey("/Metadata", ch.stream);
            ch.action = "detach";
        } else {
            ms->stream.replaceStreamData(ch.afterPacket, QPDFObjectHandle::newNull(), QPDFObjectHandle::newNull());
            QPDFObjectHandle dict = ms->stream.getDict();
            dict.replaceKey("/Type", QPDFObjectHandle::newName("/Metadata"));
            dict.replaceKey("/Subtype", QPDFObjectHandle::newName("/XML"));
            ch.stream = ms->stream;
            ch.action = "edit";
        }
        a.streams.push_back(std::move(ch));
    }
}

// Поля аннотаций и вложений: {"kind","address","field","op":"set"|"delete","value"}.
void applyObjectOps(QPDF& q, const json& ops, Applied& a) {
    if (!ops.is_array()) return;
    for (const auto& op : ops) {
        std::string kind = op.at("kind").get<std::string>();
        std::string address = op.at("address").get<std::string>();
        std::string field = op.at("field").get<std::string>();
        std::string action = op.value("op", "set");
        const ObjectField* f = findObjectField(kind, field);
        if (!f)
            throw WorkerError("bad_request", "Поле «" + field + "» объекта «" + kind +
                                                 "» не редактируется (текст комментария и байты вложения не меняются)");
        if (action != "set" && action != "delete") throw WorkerError("bad_request", "Неизвестная операция: " + action);
        json value = action == "delete" ? json(nullptr) : op.at("value");
        ObjectChange* ch = nullptr;
        for (auto& c : a.objects)
            if (c.kind == kind && c.address == address && c.field == f) ch = &c;
        if (!ch) {
            ObjectChange c;
            c.kind = kind;
            c.address = address;
            c.field = f;
            c.label = objectLabel(q, kind, address);
            c.before = readObjectField(q, kind, address, *f);
            if (kind == "annotation") c.og = resolveAnnotation(q, address).getObjGen();
            a.objects.push_back(std::move(c));
            ch = &a.objects.back();
        }
        writeObjectField(q, kind, address, *f, value, a.notes);
        ch->after = readObjectField(q, kind, address, *f);
    }
    // Правка, вернувшая исходное значение, изменением не считается.
    std::erase_if(a.objects, [](const ObjectChange& c) { return c.before == c.after; });
}

Applied applyEdits(QPDF& q, Discovery& d, const json& edits) {
    Applied a;
    applyInfoOps(q, edits.value("info", json::array()), a);
    applyXmpTargets(q, d, edits.value("xmp", json::array()), a);
    applyObjectOps(q, edits.value("objects", json::array()), a);
    // Preserve unrelated orphan objects, but erase explicitly removed metadata
    // when no remaining object references it (including non-Metadata references).
    for (const auto& change : a.streams) {
        if (change.action != "remove" && change.action != "detach") continue;
        const auto removed = parseRef(change.originalRef);
        auto references = [&](auto&& self, QPDFObjectHandle obj) -> bool {
            if (obj.isIndirect()) return obj.getObjGen() == removed;
            if (obj.isArray()) {
                for (int i = 0; i < obj.getArrayNItems(); ++i)
                    if (self(self, obj.getArrayItem(i))) return true;
            } else if (obj.isDictionary()) {
                for (const auto& key : obj.getKeys())
                    if (self(self, obj.getKey(key))) return true;
            }
            return false;
        };
        bool used = references(references, q.getTrailer());
        for (auto obj : q.getAllObjects()) {
            if (used) break;
            if (obj.getObjGen() == removed) continue;
            if (obj.isStream()) obj = obj.getDict();
            if (obj.isDictionary()) {
                for (const auto& key : obj.getKeys())
                    if (references(references, obj.getKey(key))) { used = true; break; }
            } else if (obj.isArray()) {
                for (int i = 0; i < obj.getArrayNItems(); ++i)
                    if (references(references, obj.getArrayItem(i))) { used = true; break; }
            }
        }
        if (!used) q.replaceObject(removed, QPDFObjectHandle::newNull());
    }
    return a;
}

json packetJson(const std::string& packet) {
    if (isValidUtf8(packet)) return packet;
    return json{{"base64", toBase64(packet)}};
}

json appliedToJson(const Applied& a) {
    json streams = json::array();
    for (const auto& s : a.streams) {
        json owners = json::array();
        for (const auto& o : s.owners) owners.push_back(o.toJson());
        json j{{"action", s.action}, {"stream", s.originalRef}, {"owners", owners}};
        j["before"] = json{{"packet", packetJson(s.beforePacket)}, {"model", s.beforeModel}};
        if (s.action != "remove") j["after"] = json{{"packet", packetJson(s.afterPacket)}, {"model", s.afterModel}};
        streams.push_back(j);
    }
    json objects = json::array();
    for (const auto& o : a.objects)
        objects.push_back(json{{"kind", o.kind}, {"address", o.address}, {"label", o.label}, {"field", o.field->field},
                               {"key", o.field->key}, {"fieldLabel", o.field->label}, {"before", o.before},
                               {"after", o.after}});
    return json{{"info", {{"changed", a.infoChanged}, {"before", a.infoBefore}, {"after", a.infoAfter}}},
                {"xmp", streams},
                {"objects", objects},
                {"notes", a.notes}};
}

void checkFingerprint(const fs::path& path, const json& req, Context& ctx) {
    if (!req.contains("expect")) throw WorkerError("bad_request", "Не передан отпечаток исходного файла");
    Fingerprint expected = Fingerprint::fromJson(req["expect"]);
    Fingerprint actual = computeFingerprint(path, &ctx);
    if (!(actual == expected))
        throw WorkerError("external_change", "Файл изменён другой программой после открытия. Откройте его заново.");
}

class WriterProgress : public QPDFWriter::ProgressReporter {
public:
    explicit WriterProgress(Context& c) : ctx_(c) {}
    void reportProgress(int pct) override {
        ctx_.progress("write", pct);
        ctx_.checkCancel();
    }
private:
    Context& ctx_;
};

struct TempGuard {
    fs::path path;
    bool keep = false;
    ~TempGuard() {
        if (!path.empty() && !keep) {
            std::error_code ec;
            fs::remove(path, ec);
        }
    }
};

std::string timestamp() {
    auto t = std::time(nullptr);
    std::tm tm{};
#ifdef _WIN32
    localtime_s(&tm, &t);
#else
    localtime_r(&t, &tm);
#endif
    char buf[32];
    std::strftime(buf, sizeof(buf), "%Y%m%d-%H%M%S", &tm);
    return buf;
}

}  // namespace

json openDocument(const json& req, Context& ctx) {
    fs::path path = requirePath(req, "path");
    Fingerprint fp = computeFingerprint(path, &ctx);
    LoadedPdf pdf = openPdf(path, getPassword(req), &ctx);
    json out = inspect(pdf, ctx);
    out["file"] = json{{"path", pathToUtf8(path)}, {"name", pathToUtf8(path.filename())}, {"fingerprint", fp.toJson()}};
    return out;
}

// Для частных данных приложений (/PieceInfo) адаптеров пока нет: совместимость с правкой
// неизвестна, поэтому разрешена только запись в отдельную копию (TASK.md, раздел 6).
const char* const kPrivateDataNote =
    "В документе есть частные данные приложений (/PieceInfo). Их совместимость с правкой неизвестна: "
    "они переносятся без изменений, сохранение возможно только в отдельную копию";

json previewEdits(const json& req, Context& ctx) {
    fs::path path = requirePath(req, "path");
    LoadedPdf pdf = openPdf(path, getPassword(req), &ctx);
    Discovery d = discover(*pdf.q, &ctx);
    Applied a = applyEdits(*pdf.q, d, req.value("edits", json::object()));
    json out = appliedToJson(a);
    if (a.needsPdf14 && pdf.q->getPDFVersion() < std::string("1.4"))
        out["notes"].push_back("Версия PDF будет повышена до 1.4 (нужна для потоков XMP)");
    if (!d.pieceInfo.empty())
        out["notes"].push_back(kPrivateDataNote);
    return out;
}

json saveEdits(const json& req, Context& ctx) {
    fs::path src = requirePath(req, "path");
    std::string mode = req.value("mode", "copy");
    if (mode != "copy" && mode != "replace") throw WorkerError("bad_request", "mode: copy или replace");
    const json options = req.value("options", json::object());
    std::string password = getPassword(req);

    ctx.progress("check", 0);
    checkFingerprint(src, req, ctx);
    Fingerprint srcFp = Fingerprint::fromJson(req["expect"]);

    fs::path target = mode == "replace" ? src : requirePath(req, "target");
    if (mode == "copy" && sameFile(src, target))
        throw WorkerError("target_is_source", "Для записи поверх исходного файла выберите «Заменить оригинал…»");
    fs::path dir = target.parent_path();
    if (dir.empty()) dir = fs::current_path();
    std::error_code ec;
    if (!fs::is_directory(dir, ec)) throw WorkerError("io_error", "Каталог назначения не существует");

    // Проверка блокировки до открытия: после openPdf исходный файл держит сам worker,
    // и монопольное открытие в Windows всегда завершалось бы ошибкой «файл занят».
    if (mode == "replace") ensureWritable(src);

    LoadedPdf pdf = openPdf(src, password, &ctx);
    QPDF& q = *pdf.q;

    json sig = signatureInfo(q);
    if (sig.value("signed", false) && !(mode == "copy" && options.value("allowSignedCopy", false)))
        throw WorkerError("signed_document",
                          "Документ подписан: правки возможны только в отдельную копию с явным подтверждением");
    if (q.isEncrypted() && !q.ownerPasswordMatched() && !q.allowModifyOther())
        throw WorkerError("permission_denied", "Ограничения документа запрещают изменение. Нужен пароль владельца.");

    Discovery d = discover(q, &ctx);
    if (mode == "replace" && !d.pieceInfo.empty())
        throw WorkerError("private_data_copy_only", kPrivateDataNote);

    ensureFreeSpace(dir, srcFp.size * (mode == "replace" ? 2 : 1) + (4u << 20));

    ctx.progress("apply", 0);
    json before;
    try {
        before = structureSnapshot(q);
    } catch (const std::exception& e) {
        throw WorkerError("pdf_damaged",
                          "Структура документа повреждена, сохранность страниц нельзя проверить — запись заблокирована: " +
                              sanitizeUtf8(e.what()));
    }
    // Байты всех потоков метаданных до правки — для проверки сохранности незатронутых.
    std::vector<std::pair<QPDFObjGen, std::string>> untouched;
    Applied a = applyEdits(q, d, req.value("edits", json::object()));
    if (!a.infoChanged && a.streams.empty() && a.objects.empty())
        throw WorkerError("no_changes", "Нет изменений для записи");
    for (auto& ms : d.streams) {
        if (!ms.stream.isStream()) continue;
        bool touched = false;
        for (auto& s : a.streams)
            if (s.originalRef == refOf(ms.og) && s.action == "edit") touched = true;
        if (!touched) {
            try {
                auto packet = streamBytes(ms.stream);
                try {
                    XmpDoc::parse(packet);
                } catch (const WorkerError& error) {
                    if (mode == "replace")
                        throw WorkerError("xmp_copy_only", "Исходный XMP повреждён: исправьте пакет или сохраните отдельную копию");
                    a.notes.push_back("Исходная ошибка XMP сохранена без изменения в отдельной копии: " +
                                      refOf(ms.og) + " · " + error.what());
                }
                untouched.emplace_back(ms.og, std::move(packet));
            } catch (const WorkerError& e) {
                if (e.code == "xmp_copy_only") throw;
                throw WorkerError("verification_unavailable", "Нельзя проверить сохранность незатронутого XMP: " + refOf(ms.og));
            } catch (const std::exception&) {
                throw WorkerError("verification_unavailable", "Нельзя прочитать незатронутый XMP: " + refOf(ms.og));
            }
        }
    }
    int R0 = 0, P0 = 0;
    bool wasEncrypted = q.isEncrypted(R0, P0);
    std::string versionBefore = q.getPDFVersion();
    size_t objectsBefore = d.totalObjects;
    size_t unreachable = d.totalObjects > d.reachableObjects ? d.totalObjects - d.reachableObjects : 0;

    TempGuard backupGuard;
    fs::path backup;
    if (mode == "replace") {
        backup = uniqueSibling(dir, pathToUtf8(src.stem()) + ".backup-" + timestamp(), pathToUtf8(src.extension()));
        copyFileExact(src, backup);
        backupGuard.path = backup;
        if (computeFingerprint(backup, &ctx).sha256 != srcFp.sha256)
            throw WorkerError("io_error", "Резервная копия не совпадает с оригиналом");
    }

    TempGuard tempGuard;
    tempGuard.path = tempPathIn(dir);
    std::map<std::string, QPDFObjGen> renumber;
    {
        ctx.progress("write", 0);
        std::string tmp8 = pathToUtf8(tempGuard.path);
        QPDFWriter w(q, tmp8.c_str());
        w.setLinearization(false);
        w.setObjectStreamMode(qpdf_o_preserve);
        w.setDecodeLevel(qpdf_dl_none);
        w.setCompressStreams(false);
        w.setPreserveEncryption(true);
        w.setPreserveUnreferencedObjects(true);
        if (a.needsPdf14) w.setMinimumPDFVersion("1.4");
        w.registerProgressReporter(std::make_shared<WriterProgress>(ctx));
        try {
            w.write();
        } catch (const Cancelled&) {
            throw;
        } catch (const QPDFSystemError& e) {
            if (e.getErrno() == ENOSPC)
                throw WorkerError("no_space", "Недостаточно места на диске в каталоге назначения");
            throw WorkerError("write_failed", "Ошибка записи PDF: " + sanitizeUtf8(e.what()));
        } catch (const std::exception& e) {
            throw WorkerError("write_failed", "Ошибка записи PDF: " + sanitizeUtf8(e.what()));
        }
        for (auto& s : a.streams)
            if (s.action != "remove") renumber[refOf(s.stream.getObjGen())] = w.getRenumberedObjGen(s.stream.getObjGen());
        for (auto& [og, bytes] : untouched) renumber[refOf(og)] = w.getRenumberedObjGen(og);
        for (auto& o : a.objects)
            if (o.kind == "annotation") renumber[refOf(o.og)] = w.getRenumberedObjGen(o.og);
    }
    ctx.checkCancel();

    // Повторное открытие и проверка записанного файла.
    ctx.progress("verify", 0);
    json checks = json::array();
    bool allOk = true;
    auto check = [&](const std::string& name, bool ok, const std::string& detail) {
        checks.push_back(json{{"name", name}, {"ok", ok}, {"detail", detail}});
        allOk = allOk && ok;
    };
    std::string versionAfter;
    {
        LoadedPdf out;
        try {
            out = openPdf(tempGuard.path, password, &ctx);
            check("reopen", true, "Файл открывается заново");
        } catch (const WorkerError& e) {
            check("reopen", false, e.what());
        }
        if (out.q) {
            QPDF& n = *out.q;
            versionAfter = n.getPDFVersion();
            json infoNow = infoToJson(n), infoWant = a.infoAfter;
            infoNow.erase("ref");
            infoWant.erase("ref");
            check("info", infoNow == infoWant, infoNow == infoWant ? "/Info совпадает с ожидаемым" : "/Info отличается от ожидаемого");
            for (auto& s : a.streams) {
                if (s.action == "remove") continue;
                QPDFObjectHandle ns = n.getObject(renumber[refOf(s.stream.getObjGen())]);
                bool ok = ns.isStream() && streamBytes(ns) == s.afterPacket;
                check("xmp", ok, "XMP " + (s.originalRef.empty() ? std::string("(новый)") : s.originalRef) +
                                     (ok ? " записан без расхождений" : " не совпадает с ожидаемым"));
            }
            size_t kept = 0, differ = 0;
            for (auto& [og, bytes] : untouched) {
                QPDFObjectHandle ns = n.getObject(renumber[refOf(og)]);
                if (ns.isStream() && streamBytes(ns) == bytes) ++kept; else ++differ;
            }
            check("xmp_untouched", differ == 0,
                  "Незатронутые XMP-потоки: сохранено " + std::to_string(kept) +
                      (differ ? ", изменено " + std::to_string(differ) : ""));
            for (auto& o : a.objects) {
                json now;
                try {
                    std::string addr = o.kind == "annotation" ? refOf(renumber[refOf(o.og)]) : o.address;
                    now = readObjectField(n, o.kind, addr, *o.field);
                } catch (const std::exception&) {
                    now = "\x01not-found";
                }
                bool ok = now == o.after;
                check("objects", ok, o.label + " · " + o.field->label + (ok ? ": записано" : ": не совпадает с ожидаемым"));
            }
            json after = structureSnapshot(n);
            auto same = [&](const char* name, bool ok, const std::string& good, const std::string& bad) {
                check(name, ok, ok ? good : bad);
            };
            same("pages", after["pageCount"] == before["pageCount"] && after["pageContents"] == before["pageContents"],
                 "Страницы и их содержимое не изменились", "Страницы или их содержимое отличаются от исходных");
            same("annotations", after["annotationsPerPage"] == before["annotationsPerPage"], "Аннотации на месте",
                 "Число аннотаций изменилось");
            same("annotation_contents", after["annotationContents"] == before["annotationContents"],
                 "Текст комментариев не изменился", "Текст комментариев отличается от исходного");
            same("outlines", after["outlines"] == before["outlines"], "Закладки на месте", "Закладки отличаются");
            same("forms", after["formFields"] == before["formFields"], "Поля форм на месте", "Поля форм отличаются");
            same("attachments", after["attachments"] == before["attachments"], "Вложения и их байты не изменились",
                 "Вложения отличаются от исходных");
            int R1 = 0, P1 = 0;
            bool isEnc = n.isEncrypted(R1, P1);
            bool encOk = isEnc == wasEncrypted && R1 == R0 && P1 == P0;
            check("encryption", encOk,
                  !encOk ? "Параметры шифрования изменились" : wasEncrypted ? "Шифрование сохранено" : "Документ не зашифрован");
        }
    }
    if (!allOk)
        throw WorkerError("verification_failed", "Проверка записанного файла не пройдена; исходный файл не изменён",
                          json{{"checks", checks}});

    pdf.q.reset();  // освободить исходный файл перед заменой
    ctx.progress("commit", 0);
    if (mode == "replace") {
        Fingerprint now = computeFingerprint(src, &ctx);
        if (!(now == srcFp))
            throw WorkerError("external_change", "Исходный файл изменён другой программой во время сохранения");
    }
    carryOverProtection(src, target, tempGuard.path);
    replaceFile(tempGuard.path, target);
    tempGuard.keep = true;
    backupGuard.keep = true;

    json writer{{"versionBefore", versionBefore}, {"versionAfter", versionAfter},
                {"objectsBefore", objectsBefore}, {"unreachableDropped", 0},
                {"unreachablePreserved", unreachable},
                {"notes", json::array({"Полная перезапись: номера объектов, смещения и таблица ссылок пересозданы",
                                       "Вторая часть trailer /ID обновлена; первая сохранена",
                                       "Прежние incremental revisions не перенесены"})}};
    json result{{"target", pathToUtf8(target)}, {"checks", checks}, {"writer", writer}, {"changes", appliedToJson(a)}};
    if (!backup.empty()) result["backup"] = pathToUtf8(backup);
    result["fingerprint"] = computeFingerprint(target, &ctx).toJson();
    return result;
}

}  // namespace pm
