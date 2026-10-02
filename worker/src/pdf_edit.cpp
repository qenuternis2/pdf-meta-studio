#include "pdf_edit.hpp"

#include "fileutil.hpp"
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

struct Applied {
    json infoBefore, infoAfter;
    bool infoChanged = false;
    std::vector<StreamChange> streams;
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
            for (auto& o : ms->owners)
                if (refOf(o.og) == ownerRef || (ownerRef == "catalog" && o.kind == "catalog")) affected.push_back(o);
            if (affected.empty()) throw WorkerError("bad_request", "Владелец " + ownerRef + " не использует поток " + sref);
            affected.resize(1);
        } else {
            affected = ms->owners;
        }
        ch.owners = affected;
        ch.beforePacket = streamBytes(ms->stream);

        if (action == "remove") {
            for (auto& o : affected) {
                if (!o.keyPath.empty())
                    throw WorkerError("unsupported_owner", "Удаление /Metadata из вложенного словаря пока не поддерживается");
                ownerDict(q.getObject(o.og)).removeKey("/Metadata");
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
            bool replaces = !ops.empty() && ops[0].value("op", "") == "replacePacket";
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
            if (!affected[0].keyPath.empty())
                throw WorkerError("unsupported_owner", "Отделение потока для вложенного словаря пока не поддерживается");
            ch.stream = newMetadataStream(q, ch.afterPacket);
            ownerDict(q.getObject(affected[0].og)).replaceKey("/Metadata", ch.stream);
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

Applied applyEdits(QPDF& q, Discovery& d, const json& edits) {
    Applied a;
    applyInfoOps(q, edits.value("info", json::array()), a);
    applyXmpTargets(q, d, edits.value("xmp", json::array()), a);
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
    return json{{"info", {{"changed", a.infoChanged}, {"before", a.infoBefore}, {"after", a.infoAfter}}},
                {"xmp", streams},
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
    if (!a.infoChanged && a.streams.empty())
        throw WorkerError("no_changes", "Нет изменений для записи");
    for (auto& ms : d.streams) {
        bool touched = false;
        for (auto& s : a.streams)
            if (s.originalRef == refOf(ms.og)) touched = true;
        if (!touched && ms.reachable) {
            try {
                untouched.emplace_back(ms.og, streamBytes(ms.stream));
            } catch (const std::exception&) {
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
            json after = structureSnapshot(n);
            auto same = [&](const char* name, bool ok, const std::string& good, const std::string& bad) {
                check(name, ok, ok ? good : bad);
            };
            same("pages", after["pageCount"] == before["pageCount"] && after["pageContents"] == before["pageContents"],
                 "Страницы и их содержимое не изменились", "Страницы или их содержимое отличаются от исходных");
            same("annotations", after["annotationsPerPage"] == before["annotationsPerPage"], "Аннотации на месте",
                 "Число аннотаций изменилось");
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
    replaceFile(tempGuard.path, target);
    tempGuard.keep = true;
    backupGuard.keep = true;

    json writer{{"versionBefore", versionBefore}, {"versionAfter", versionAfter},
                {"objectsBefore", objectsBefore}, {"unreachableDropped", unreachable},
                {"notes", json::array({"Полная перезапись: номера объектов, смещения и таблица ссылок пересозданы",
                                       "Вторая часть trailer /ID обновлена; первая сохранена",
                                       "Прежние incremental revisions не перенесены"})}};
    json result{{"target", pathToUtf8(target)}, {"checks", checks}, {"writer", writer}, {"changes", appliedToJson(a)}};
    if (!backup.empty()) result["backup"] = pathToUtf8(backup);
    result["fingerprint"] = computeFingerprint(target, &ctx).toJson();
    return result;
}

}  // namespace pm
