#include "private_data.hpp"
#include "logical_graph.hpp"
#include <fstream>
#include <unordered_set>
#include <qpdf/Buffer.hh>
#include <qpdf/Pipeline.hh>

namespace pm {
namespace {
class ExportSink : public Pipeline {
public:
    explicit ExportSink(size_t maximum) : Pipeline("bounded opaque export", nullptr), maximum(maximum) {}
    void write(unsigned char const* bytes, size_t count) override {
        if (count > maximum - data.size()) throw WorkerError("export_too_large", "Экспорт превышает 64 МиБ");
        data.append(reinterpret_cast<const char*>(bytes), count);
    }
    void finish() override {}
    size_t maximum;
    std::string data;
};
}
json exportMetadata(const json& request, Context& context) {
    auto source = pathFromUtf8(request.at("path").get<std::string>());
    auto readPath = pathFromUtf8(request.value("snapshotPath", request.at("path").get<std::string>()));
    auto target = pathFromUtf8(request.at("target").get<std::string>());
    for (auto path : {source, readPath}) if (sameFile(path, target) || fs::absolute(path).lexically_normal() == fs::absolute(target).lexically_normal())
        throw WorkerError("target_is_source", "Экспорт не может заменить исходный PDF или снимок сессии");
    auto pdf = openPdf(readPath, request.value("password", ""), &context);
    auto stream = pdf.q->getObject(parseRef(request.at("stream").get<std::string>()));
    if (!stream.isStream()) throw WorkerError("bad_request", "Поток метаданных не найден в исходном снимке");
    auto bytes = streamBytes(stream);
    auto temporary = tempPathIn(target.has_parent_path() ? target.parent_path() : fs::current_path());
    try {
        std::ofstream output(temporary, std::ios::binary); output.write(bytes.data(), bytes.size()); output.close();
        if (!output) throw WorkerError("write_failed", "Не удалось сохранить исходные байты XMP");
        carryOverProtection(fs::exists(source) ? source : readPath, target, temporary); replaceFile(temporary, target);
    } catch (...) { std::error_code ignored; fs::remove(temporary, ignored); throw; }
    return json{{"target", pathToUtf8(target)}, {"bytes", bytes.size()}};
}

std::string privateAddress(const std::string& owner, const json& path, const std::string& application) {
    return json{{"owner", owner}, {"path", path}, {"app", application}}.dump();
}
QPDFObjectHandle privateEntry(QPDF& pdf, const std::string& address) {
    json location;
    try { location = json::parse(address); }
    catch (const std::exception&) { throw WorkerError("bad_request", "Некорректный адрес частного контейнера"); }
    auto value = pdf.getObject(parseRef(location.at("owner").get<std::string>()));
    for (const auto& step : location.at("path")) {
        if (value.isStream()) value = value.getDict();
        if (step.is_string() && value.isDictionary()) value = value.getKey(step.get<std::string>());
        else if (step.is_number_integer() && value.isArray()) value = value.getArrayItem(step.get<int>());
        else throw WorkerError("bad_request", "Не найден владелец частного контейнера");
    }
    if (value.isStream()) value = value.getDict();
    if (!value.isDictionary()) throw WorkerError("bad_request", "Владелец частного контейнера не является словарём");
    auto entry = value.getKey("/PieceInfo").getKey(location.at("app").get<std::string>());
    if (!entry.isDictionary()) throw WorkerError("bad_request", "Частный контейнер не найден");
    return entry;
}
bool supportedPrivateEntry(QPDFObjectHandle entry) {
    for (const auto& key : entry.getKeys())
        if (key != "/Private" && key != "/LastModified") return false;
    if (entry.hasKey("/LastModified") && !entry.getKey("/LastModified").isString()) return false;
    auto value = entry.getKey("/Private");
    if (!value.isDictionary()) return false;
    auto schema = value.getKey("/Schema");
    if (!schema.isName() || schema.getName() != "/PdfMetaStudioV1") return false;
    // This registered schema contains descriptive strings only, never embedded offsets/references.
    for (const auto& key : value.getKeys()) {
        if (key == "/Schema") continue;
        if (key != "/Label" && key != "/Description") return false;
        if (!value.getKey(key).isString()) return false;
    }
    return true;
}
json privateFields(QPDFObjectHandle entry) {
    json fields = json::object();
    for (auto [field, key] : {std::pair{"label", "/Label"}, {"description", "/Description"}, {"modified", "/LastModified"}}) {
        auto value = std::string(field) == "modified" ? entry.getKey(key) : entry.getKey("/Private").getKey(key);
        fields[field] = value.isString() ? json(sanitizeUtf8(value.getUTF8Value())) : json(nullptr);
    }
    return fields;
}
json exportPrivate(const json& request, Context& context) {
    auto readPath = pathFromUtf8(request.value("snapshotPath", request.at("path").get<std::string>()));
    auto pdf = openPdf(readPath, request.value("password", ""), &context);
    auto entry = privateEntry(*pdf.q, request.at("address").get<std::string>());
    auto value = entry.getKey("/Private");
    auto target = pathFromUtf8(request.at("target").get<std::string>());
    auto source = pathFromUtf8(request.at("path").get<std::string>());
    if (sameFile(source, target) || sameFile(readPath, target) || fs::absolute(source).lexically_normal() == fs::absolute(target).lexically_normal() || fs::absolute(readPath).lexically_normal() == fs::absolute(target).lexically_normal())
        throw WorkerError("target_is_source", "Экспорт не может заменить исходный PDF");
    json result{{"format", "PDF Meta Studio private export/1"}, {"address", request.at("address")},
                {"root", pdfValueTree(value)}, {"objects", json::object()}};
    std::unordered_set<std::string> seen;
    std::vector<QPDFObjectHandle> queue{value};
    size_t bytes = 0, visited = 0;
    while (!queue.empty()) {
        context.checkCancel();
        if (++visited > 100000) throw WorkerError("export_too_large", "Экспорт превышает 100 000 объектов");
        auto object = queue.back(); queue.pop_back();
        if (object.isIndirect()) {
            auto reference = refOf(object.getObjGen());
            if (!seen.insert(reference).second) continue;
            json diagnostic = pdfValueTree(object);
            if (object.isStream()) {
                ExportSink sink((64ull << 20) - bytes);
                if (!object.pipeStreamData(&sink, nullptr, 0, qpdf_dl_none, true)) throw WorkerError("export_failed", "Не удалось прочитать непрозрачный поток");
                bytes += sink.data.size();
                diagnostic["rawStreamBase64"] = toBase64(sink.data);
            }
            result["objects"][reference] = diagnostic;
        }
        if (object.isStream()) object = object.getDict();
        if (queue.size() > 100000) throw WorkerError("export_too_large", "Превышен лимит очереди экспорта");
        if (object.isDictionary()) for (const auto& key : object.getKeys()) queue.push_back(object.getKey(key));
        else if (object.isArray()) for (int index = 0; index < object.getArrayNItems(); ++index) queue.push_back(object.getArrayItem(index));
    }
    auto temporary = tempPathIn(target.has_parent_path() ? target.parent_path() : fs::current_path());
    try {
        std::ofstream output(temporary, std::ios::binary);
        auto serialized = result.dump(2);
        if (serialized.size() > (128ull << 20)) throw WorkerError("export_too_large", "Экспорт превышает 128 МиБ");
        output << serialized;
        output.close();
        if (!output) throw WorkerError("write_failed", "Не удалось записать экспорт");
        carryOverProtection(fs::exists(source) ? source : pathFromUtf8(request.at("snapshotPath").get<std::string>()), target, temporary);
        replaceFile(temporary, target);
    } catch (...) { std::error_code ignored; fs::remove(temporary, ignored); throw; }
    return json{{"target", pathToUtf8(target)}, {"objects", seen.size()}, {"compatibility", "not verified; opaque bytes may contain internal offsets"}};
}
}
