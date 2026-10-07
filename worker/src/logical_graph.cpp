#include "logical_graph.hpp"
#include "sha256.hpp"
#include <qpdf/Pipeline.hh>
#include <algorithm>

namespace pm {
namespace {
constexpr size_t maximumNodes = 1000000;
class HashSink : public Pipeline {
public:
    explicit HashSink(Context& c) : Pipeline("logical stream fingerprint", nullptr), context(c) {}
    void write(unsigned char const* bytes, size_t length) override {
        context.checkCancel();
        if (length > (1ull << 30) - count) throw WorkerError("verification_unavailable", "Поток превышает лимит проверки 1 ГиБ");
        count += length;
        digest.update(bytes, length);
    }
    void finish() override {}
    Context& context;
    Sha256 digest;
    size_t count = 0;
};

json tree(QPDFObjectHandle value, bool resolve, size_t depth, size_t& nodes, const ReferenceMap* reverse, Context* context) {
    if (++nodes > maximumNodes || depth > 128)
        throw WorkerError("verification_unavailable", "Превышен лимит дерева PDF: 1 000 000 элементов или 128 уровней");
    if (context && nodes % 1000 == 0) context->checkCancel();
    if (value.isIndirect() && !resolve && (value.isArray() || value.isDictionary() || value.isStream())) {
        std::string reference = refOf(value.getObjGen());
        if (reverse) {
            auto it = reverse->find(reference);
            if (it == reverse->end()) throw WorkerError("verification_failed", "Обнаружена новая необъяснённая ссылка: " + reference);
            reference = refOf(it->second);
        }
        return json{{"reference", reference}};
    }
    json result{{"type", value.getTypeName()}};
    if (value.isString()) {
        result["bytes"] = toHex(value.getStringValue());
        if (!reverse && !context) result["text"] = sanitizeUtf8(value.getUTF8Value());
    } else if (value.isName()) result["bytes"] = toHex(value.getName());
    else if (value.isBool()) result["value"] = value.getBoolValue();
    else if (value.isNumber()) {
        // qpdf preserves numeric spellings; exact representation also avoids float rounding.
        result["value"] = value.unparse();
    } else if (value.isArray()) {
        result["items"] = json::array();
        for (int i = 0; i < value.getArrayNItems(); ++i)
            result["items"].push_back(tree(value.getArrayItem(i), false, depth + 1, nodes, reverse, context));
    } else if (value.isDictionary() || value.isStream()) {
        auto dict = value.isStream() ? value.getDict() : value;
        result["entries"] = json::object();
        for (const auto& key : dict.getKeys()) {
            auto child = dict.getKey(key);
            if (child.isNull() || (value.isStream() && key == "/Length")) continue;
            // qpdf removes an empty DecodeParms list; no decoding parameters are lost.
            if (value.isStream() && key == "/DecodeParms" && child.isArray() && child.getArrayNItems() == 0) continue;
            // Missing and empty page contents both contain zero content streams.
            if (dict.getKey("/Type").isName() && dict.getKey("/Type").getName() == "/Page" &&
                key == "/Contents" && child.isArray() && child.getArrayNItems() == 0) continue;
            // qpdf explicitly materializes the Extensions and ADBE dictionaries. Compare their complete
            // values inline here; the original indirect objects remain independently fingerprinted.
            if (key == "/Extensions" && dict.getKey("/Type").isName() && dict.getKey("/Type").getName() == "/Catalog" && child.isDictionary()) {
                auto extensions = child.shallowCopy();
                auto adobe = extensions.getKey("/ADBE");
                if (adobe.isDictionary() && adobe.isIndirect()) extensions.replaceKey("/ADBE", adobe.shallowCopy());
                result["entries"][toHex(key)] = tree(extensions, true, depth + 1, nodes, reverse, context);
                continue;
            }
            result["entries"][toHex(key)] = tree(child, false, depth + 1, nodes, reverse, context);
        }
        if (value.isStream() && context) {
            HashSink sink(*context);
            if (!value.pipeStreamData(&sink, nullptr, 0, qpdf_dl_none, true))
                throw WorkerError("verification_unavailable", "Нельзя прочитать поток при проверке структуры");
            result["streamSha256"] = sink.digest.hexDigest();
            result["streamBytes"] = sink.count;
        }
    } else if (!value.isNull()) result["raw"] = toHex(value.unparse());
    return result;
}

bool managed(QPDFObjectHandle object, QPDFObjGen encryption) {
    if (object.getObjGen() == encryption && encryption.isIndirect()) return true;
    auto dictionary = object.isStream() ? object.getDict() : object;
    if (!dictionary.isDictionary()) return false;
    auto type = dictionary.getKey("/Type");
    return dictionary.hasKey("/Linearized") || (type.isName() && (type.getName() == "/XRef" || type.getName() == "/ObjStm"));
}
std::string digest(QPDFObjectHandle object, size_t& budget, const ReferenceMap* reverse, Context& context) {
    return Sha256::hex(tree(object, true, 0, budget, reverse, &context).dump());
}
QPDFObjectHandle trailerForCheck(QPDF& pdf) {
    auto trailer = pdf.getTrailer().shallowCopy();
    for (const char* key : {"/ID", "/Size", "/Prev", "/XRefStm", "/Encrypt"}) trailer.removeKey(key);
    auto type = trailer.getKey("/Type");
    if (type.isName() && type.getName() == "/XRef")
        for (const char* key : {"/Type", "/W", "/Index", "/Length", "/Filter", "/DecodeParms"}) trailer.removeKey(key);
    return trailer;
}
}
json pdfValueTree(QPDFObjectHandle value, bool resolveRoot, size_t depth) {
    size_t nodes = 0;
    return tree(value, resolveRoot, depth, nodes, nullptr, nullptr);
}

json logicalGraph(QPDF& pdf, Context& context, const ReferenceMap* reverse) {
    json result{{"objects", json::object()}};
    size_t budget = 0;
    auto encryption = pdf.getTrailer().getKey("/Encrypt").getObjGen();
    std::set<std::string> lengths;
    for (auto object : pdf.getAllObjects()) if (object.isStream()) {
        auto length = object.getDict().getKey("/Length");
        if (length.isIndirect() && length.isInteger()) lengths.insert(refOf(length.getObjGen()));
    }
    for (auto object : pdf.getAllObjects()) {
        context.checkCancel();
        if (managed(object, encryption) || lengths.count(refOf(object.getObjGen()))) continue;
        std::string reference = refOf(object.getObjGen());
        if (reverse) reference = refOf(reverse->at(reference));
        result["objects"][reference] = digest(object, budget, reverse, context);
    }
    result["trailer"] = digest(trailerForCheck(pdf), budget, reverse, context);
    return result;
}

json checkLogicalGraph(QPDF& pdf, const json& expected, const ReferenceMap& renumber, Context& context) {
    ReferenceMap reverse;
    bool collision = false;
    for (const auto& [source, target] : renumber) {
        if (!target.isIndirect()) continue;
        if (!reverse.emplace(refOf(target), parseRef(source)).second) collision = true;
    }
    size_t budget = 0;
    json differences = json::array();
    for (auto it = expected.at("objects").begin(); it != expected.at("objects").end(); ++it) {
        context.checkCancel();
        auto target = renumber.find(it.key());
        if (target == renumber.end() || !target->second.isIndirect()) { differences.push_back(it.key()); continue; }
        if (digest(pdf.getObject(target->second), budget, &reverse, context) != it.value().get<std::string>()) differences.push_back(it.key());
    }
    if (digest(trailerForCheck(pdf), budget, &reverse, context) != expected.at("trailer").get<std::string>()) differences.push_back("trailer");
    return json{{"ok", differences.empty() && !collision}, {"differences", differences}, {"referenceCollision", collision},
                {"objects", expected.at("objects").size()}};
}
}
