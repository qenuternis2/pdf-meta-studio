#include "xmp_model.hpp"

#include <XMP.incl_cpp>
#include <XMP.hpp>

#include <algorithm>
#include <map>
#include <cctype>
#include <vector>

namespace pm {

using Meta = SXMPMeta;

// По умолчанию XMP Core молча пропускает «восстановимые» ошибки XML (например, незакрытый тег)
// и строит частичную модель. В строгом режиме любая ошибка прерывает разбор.
static bool gStrictParse = true;
static std::string gLastXmpError;

static bool xmpErrorCallback(void*, XMP_ErrorSeverity, XMP_Int32, XMP_StringPtr message) {
    gLastXmpError = message ? message : "";
    return !gStrictParse;  // false — SDK бросает исключение
}

void xmpInitialize() {
    if (!SXMPMeta::Initialize()) throw WorkerError("internal", "Не удалось инициализировать XMP Core");
    SXMPMeta::SetDefaultErrorCallback(xmpErrorCallback, nullptr, 1000);
}

void xmpTerminate() { SXMPMeta::Terminate(); }

std::string xmpToolkitVersion() {
    XMP_VersionInfo v;
    SXMPMeta::GetVersionInfo(&v);
    return v.message ? v.message : "";
}

void precheckPacket(const std::string& packet) {
    if (packet.size() > kMaxXmpPacketBytes)
        throw WorkerError("xmp_too_large", "XMP-пакет превышает допустимый размер");
    std::string lower;
    lower.reserve(packet.size());
    for (char c : packet) lower += static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    if (lower.find("<!doctype") != std::string::npos || lower.find("<!entity") != std::string::npos)
        throw WorkerError("xmp_forbidden_dtd", "XMP содержит DOCTYPE или объявление сущностей — это запрещено");
}

struct XmpDoc::Impl {
    Meta meta;
};

XmpDoc::XmpDoc() : impl_(std::make_unique<Impl>()) {}
XmpDoc::~XmpDoc() = default;

std::unique_ptr<XmpDoc> XmpDoc::empty() { return std::make_unique<XmpDoc>(); }

std::unique_ptr<XmpDoc> XmpDoc::parse(const std::string& packet, bool strict) {
    precheckPacket(packet);
    gStrictParse = strict;
    gLastXmpError.clear();
    struct Restore { ~Restore() { gStrictParse = true; } } restore;
    auto doc = std::make_unique<XmpDoc>();
    try {
        doc->impl_->meta.ParseFromBuffer(packet.data(), static_cast<XMP_StringLen>(packet.size()),
                                         kXMP_RequireXMPMeta);
    } catch (const XMP_Error& e) {
        std::string msg = e.GetErrMsg() ? e.GetErrMsg() : "";
        if (!gLastXmpError.empty() && msg.find(gLastXmpError) == std::string::npos) msg += " (" + gLastXmpError + ")";
        throw WorkerError("xmp_invalid", "XMP не разобран: " + msg,
                          json{{"xmpErrorId", e.GetID()}});
    }
    return doc;
}

// ---------------------------------------------------------------------------
// Пути: SDK-путь вида  pfx:name[2]/pfx:field/?pfx:qual  <->  шаги с URI.

static std::string prefixOf(const std::string& uri, const std::string& hint) {
    std::string prefix;
    if (Meta::GetNamespacePrefix(uri.c_str(), &prefix)) {
        if (!prefix.empty() && prefix.back() == ':') prefix.pop_back();
        return prefix;
    }
    std::string suggested = hint.empty() ? "ns" : hint;
    Meta::RegisterNamespace(uri.c_str(), suggested.c_str(), &prefix);
    if (!prefix.empty() && prefix.back() == ':') prefix.pop_back();
    return prefix;
}

static std::string uriOf(const std::string& prefix) {
    std::string uri;
    if (!Meta::GetNamespaceURI(prefix.c_str(), &uri))
        throw WorkerError("internal", "Неизвестный префикс XMP: " + prefix);
    return uri;
}

static json parseSdkPath(const std::string& path) {
    json steps = json::array();
    size_t i = 0;
    bool first = true;
    auto readName = [&](size_t& pos) {
        size_t start = pos;
        while (pos < path.size() && path[pos] != '/' && path[pos] != '[') ++pos;
        return path.substr(start, pos - start);
    };
    while (i < path.size()) {
        if (path[i] == '[') {
            size_t close = path.find(']', i);
            if (close == std::string::npos) throw WorkerError("internal", "Некорректный путь XMP: " + path);
            steps.push_back(json{{"t", "item"}, {"i", std::stoi(path.substr(i + 1, close - i - 1))}});
            i = close + 1;
            continue;
        }
        std::string kind = first ? "prop" : "field";
        if (path[i] == '/') {
            ++i;
            if (i < path.size() && path[i] == '?') {
                kind = "qual";
                ++i;
            }
        }
        std::string qname = readName(i);
        auto colon = qname.find(':');
        if (colon == std::string::npos) throw WorkerError("internal", "Путь XMP без префикса: " + path);
        std::string prefix = qname.substr(0, colon);
        steps.push_back(json{{"t", kind}, {"ns", uriOf(prefix)}, {"name", qname.substr(colon + 1)}, {"prefix", prefix}});
        first = false;
    }
    return steps;
}

struct Target {
    std::string schemaNS;
    std::string path;
};

static Target composePath(const json& steps) {
    if (!steps.is_array() || steps.empty() || steps[0].value("t", "") != "prop")
        throw WorkerError("bad_request", "Путь XMP должен начинаться со свойства верхнего уровня");
    Target t;
    t.schemaNS = steps[0].at("ns").get<std::string>();
    prefixOf(t.schemaNS, steps[0].value("prefix", ""));
    t.path = steps[0].at("name").get<std::string>();
    for (size_t k = 1; k < steps.size(); ++k) {
        const auto& s = steps[k];
        std::string kind = s.at("t").get<std::string>();
        std::string out;
        if (kind == "item") {
            SXMPUtils::ComposeArrayItemPath(t.schemaNS.c_str(), t.path.c_str(), s.at("i").get<int>(), &out);
        } else if (kind == "field" || kind == "qual") {
            std::string ns = s.at("ns").get<std::string>();
            prefixOf(ns, s.value("prefix", ""));
            std::string name = s.at("name").get<std::string>();
            if (kind == "field")
                SXMPUtils::ComposeStructFieldPath(t.schemaNS.c_str(), t.path.c_str(), ns.c_str(), name.c_str(), &out);
            else
                SXMPUtils::ComposeQualifierPath(t.schemaNS.c_str(), t.path.c_str(), ns.c_str(), name.c_str(), &out);
        } else {
            throw WorkerError("bad_request", "Неизвестный шаг пути XMP: " + kind);
        }
        t.path = out;
    }
    return t;
}

static const char* formName(XMP_OptionBits o) {
    if (o & kXMP_PropArrayIsAltText) return "altText";
    if (o & kXMP_PropArrayIsAlternate) return "alt";
    if (o & kXMP_PropArrayIsOrdered) return "seq";
    if (o & kXMP_PropValueIsArray) return "bag";
    if (o & kXMP_PropValueIsStruct) return "struct";
    return "simple";
}

static XMP_OptionBits formBits(const std::string& form) {
    if (form == "simple") return 0;
    if (form == "struct") return kXMP_PropValueIsStruct;
    if (form == "bag") return kXMP_PropValueIsArray;
    if (form == "seq") return kXMP_PropValueIsArray | kXMP_PropArrayIsOrdered;
    if (form == "alt") return kXMP_PropValueIsArray | kXMP_PropArrayIsOrdered | kXMP_PropArrayIsAlternate;
    if (form == "altText")
        return kXMP_PropValueIsArray | kXMP_PropArrayIsOrdered | kXMP_PropArrayIsAlternate | kXMP_PropArrayIsAltText;
    throw WorkerError("bad_request", "Неизвестный тип XMP-узла: " + form);
}

json XmpDoc::model() const {
    json nodes = json::array();
    std::vector<std::string> uris;
    SXMPIterator it(impl_->meta);
    std::string schemaNS, propPath, propValue;
    XMP_OptionBits opts = 0;
    while (it.Next(&schemaNS, &propPath, &propValue, &opts)) {
        if (std::find(uris.begin(), uris.end(), schemaNS) == uris.end()) uris.push_back(schemaNS);
        if (opts & kXMP_SchemaNode) continue;
        json n;
        n["ns"] = schemaNS;
        n["path"] = propPath;
        n["steps"] = parseSdkPath(propPath);
        n["form"] = formName(opts);
        if (XMP_PropIsSimple(opts)) n["value"] = sanitizeUtf8(propValue);
        if (opts & kXMP_PropValueIsURI) n["uri"] = true;
        if (opts & kXMP_PropIsQualifier) n["qualifier"] = true;
        if (opts & kXMP_PropHasQualifiers) n["hasQualifiers"] = true;
        if (opts & kXMP_PropHasLang) n["hasLang"] = true;
        for (const auto& s : n["steps"]) {
            if (s.contains("ns")) {
                std::string u = s["ns"].get<std::string>();
                if (std::find(uris.begin(), uris.end(), u) == uris.end()) uris.push_back(u);
            }
        }
        nodes.push_back(std::move(n));
    }
    json namespaces = json::array();
    for (const auto& u : uris) {
        std::string prefix;
        Meta::GetNamespacePrefix(u.c_str(), &prefix);
        if (!prefix.empty() && prefix.back() == ':') prefix.pop_back();
        namespaces.push_back(json{{"uri", u}, {"prefix", prefix}});
    }
    std::string about;
    impl_->meta.GetObjectName(&about);
    return json{{"about", about}, {"namespaces", namespaces}, {"nodes", nodes}};
}

std::string XmpDoc::serialize() const {
    std::string out;
    // Пакет с обёрткой <?xpacket?> и стандартным запасом под правки на месте.
    impl_->meta.SerializeToBuffer(&out, kXMP_UseCompactFormat, 2048);
    // Контроль: повторный разбор должен дать ту же семантику (узлы по пути из URI и индексов;
    // порядок свойств и полей структур не важен, порядок элементов массивов важен).
    // Иначе SDK изменил данные при сериализации (например, переводы) — такой пакет не пишем.
    auto semantic = [](const json& m) {
        std::map<std::string, std::string> out;
        for (const auto& n : m["nodes"]) {
            json key = json::array();
            for (const auto& st : n["steps"]) key.push_back(json{st.value("t", ""), st.value("ns", ""), st.value("name", ""), st.value("i", 0)});
            json val{n.value("form", ""), n.value("value", ""), n.value("uri", false)};
            out[key.dump()] = val.dump();
        }
        out["@about"] = m.value("about", "");
        return out;
    };
    auto mine = semantic(model()), again = semantic(parse(out)->model());
    if (mine != again) {
        json diff = json::array();
        for (const auto& [k, v] : mine) {
            auto it = again.find(k);
            if (it == again.end() || it->second != v) diff.push_back(k);
        }
        for (const auto& [k, v] : again)
            if (!mine.count(k)) diff.push_back(k);
        throw WorkerError("xmp_roundtrip_mismatch",
                          "Сериализация XMP изменила бы данные сверх запрошенных правок; запись остановлена",
                          json{{"paths", diff}});
    }
    return out;
}

bool XmpDoc::getSimple(const std::string& ns, const std::string& name, std::string& out) const {
    XMP_OptionBits o = 0;
    prefixOf(ns, "");
    if (!impl_->meta.GetProperty(ns.c_str(), name.c_str(), &out, &o)) return false;
    if (XMP_PropIsSimple(o)) return true;
    if (o & kXMP_PropArrayIsAltText) {
        std::string actual;
        return impl_->meta.GetLocalizedText(ns.c_str(), name.c_str(), "", "x-default", &actual, &out, nullptr);
    }
    return false;
}

static std::string lowerAscii(std::string s) {
    for (auto& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}

// Найти элемент Alt-text с данным xml:lang (без побочного изменения других языков).
static int findLangItem(Meta& meta, const Target& t, const std::string& lang) {
    int count = meta.CountArrayItems(t.schemaNS.c_str(), t.path.c_str());
    for (int i = 1; i <= count; ++i) {
        std::string item, q;
        SXMPUtils::ComposeArrayItemPath(t.schemaNS.c_str(), t.path.c_str(), i, &item);
        if (meta.GetQualifier(t.schemaNS.c_str(), item.c_str(), kXMP_NS_XML, "lang", &q, nullptr) &&
            lowerAscii(q) == lowerAscii(lang))
            return i;
    }
    return 0;
}

static void opSetLangAlt(Meta& meta, const Target& t, const std::string& lang, const std::string& value) {
    XMP_OptionBits o = 0;
    if (!meta.GetProperty(t.schemaNS.c_str(), t.path.c_str(), nullptr, &o)) {
        meta.SetProperty(t.schemaNS.c_str(), t.path.c_str(), nullptr, formBits("altText"));
    } else if (!(o & kXMP_PropValueIsArray)) {
        throw WorkerError("xmp_op_failed", "Свойство " + t.path + " не является списком языковых вариантов");
    }
    int idx = findLangItem(meta, t, lang);
    std::string item;
    if (idx > 0) {
        SXMPUtils::ComposeArrayItemPath(t.schemaNS.c_str(), t.path.c_str(), idx, &item);
        XMP_OptionBits io = 0;
        meta.GetProperty(t.schemaNS.c_str(), item.c_str(), nullptr, &io);
        meta.SetProperty(t.schemaNS.c_str(), item.c_str(), value.c_str(), io & kXMP_PropValueIsURI);
        return;
    }
    int count = meta.CountArrayItems(t.schemaNS.c_str(), t.path.c_str());
    if (lang == "x-default" && count > 0) {
        meta.SetArrayItem(t.schemaNS.c_str(), t.path.c_str(), 1, value.c_str(), kXMP_InsertBeforeItem);
        idx = 1;
    } else {
        meta.AppendArrayItem(t.schemaNS.c_str(), t.path.c_str(), formBits("altText"), value.c_str(), 0);
        idx = count + 1;
    }
    SXMPUtils::ComposeArrayItemPath(t.schemaNS.c_str(), t.path.c_str(), idx, &item);
    meta.SetQualifier(t.schemaNS.c_str(), item.c_str(), kXMP_NS_XML, "lang", lang.c_str());
}

static void opSetArray(Meta& meta, const Target& t, const std::string& form, const json& items) {
    XMP_OptionBits want = formBits(form);
    XMP_OptionBits o = 0;
    bool exists = meta.GetProperty(t.schemaNS.c_str(), t.path.c_str(), nullptr, &o);
    if (exists && (o & kXMP_PropArrayFormMask) != (want & kXMP_PropArrayFormMask)) {
        meta.DeleteProperty(t.schemaNS.c_str(), t.path.c_str());
        exists = false;
    }
    if (!exists) meta.SetProperty(t.schemaNS.c_str(), t.path.c_str(), nullptr, want);
    int count = meta.CountArrayItems(t.schemaNS.c_str(), t.path.c_str());
    int n = static_cast<int>(items.size());
    // Совпадающие позиции обновляются на месте (квалификаторы элементов сохраняются).
    for (int i = 1; i <= std::min(count, n); ++i) {
        std::string item, cur;
        SXMPUtils::ComposeArrayItemPath(t.schemaNS.c_str(), t.path.c_str(), i, &item);
        XMP_OptionBits io = 0;
        meta.GetProperty(t.schemaNS.c_str(), item.c_str(), &cur, &io);
        if (!XMP_PropIsSimple(io))
            throw WorkerError("xmp_op_failed", "Элемент " + item + " не является простым значением");
        std::string v = items[static_cast<size_t>(i - 1)].get<std::string>();
        if (cur != v) meta.SetProperty(t.schemaNS.c_str(), item.c_str(), v.c_str(), io & kXMP_PropValueIsURI);
    }
    for (int i = count; i > n; --i) {
        std::string item;
        SXMPUtils::ComposeArrayItemPath(t.schemaNS.c_str(), t.path.c_str(), i, &item);
        meta.DeleteProperty(t.schemaNS.c_str(), item.c_str());
    }
    for (int i = count; i < n; ++i)
        meta.AppendArrayItem(t.schemaNS.c_str(), t.path.c_str(), want,
                             items[static_cast<size_t>(i)].get<std::string>().c_str(), 0);
}

static void applyOne(Meta& meta, const json& op) {
    std::string kind = op.at("op").get<std::string>();
    if (kind == "replacePacket") {
        std::string xml = op.at("xml").get<std::string>();
        precheckPacket(xml);
        Meta fresh;  // ошибка разбора XML/RDF прерывает всю транзакцию
        fresh.ParseFromBuffer(xml.data(), static_cast<XMP_StringLen>(xml.size()), kXMP_RequireXMPMeta);
        meta = fresh;
        return;
    }
    Target t = composePath(op.at("steps"));
    const char* ns = t.schemaNS.c_str();
    const char* path = t.path.c_str();
    if (kind == "set") {
        XMP_OptionBits o = 0;
        if (!meta.GetProperty(ns, path, nullptr, &o))
            throw WorkerError("xmp_op_failed", "Свойство не найдено: " + t.path);
        if (!XMP_PropIsSimple(o))
            throw WorkerError("xmp_op_failed", "Свойство не является простым значением: " + t.path);
        XMP_OptionBits keep = o & kXMP_PropValueIsURI;
        if (op.contains("uri")) keep = op["uri"].get<bool>() ? kXMP_PropValueIsURI : 0;
        meta.SetProperty(ns, path, op.at("value").get<std::string>().c_str(), keep);
    } else if (kind == "create") {
        if (meta.DoesPropertyExist(ns, path))
            throw WorkerError("xmp_op_failed", "Свойство уже существует: " + t.path);
        std::string form = op.value("form", "simple");
        XMP_OptionBits bits = formBits(form);
        if (form == "simple") {
            if (op.value("uri", false)) bits |= kXMP_PropValueIsURI;
            meta.SetProperty(ns, path, op.value("value", "").c_str(), bits);
        } else {
            meta.SetProperty(ns, path, nullptr, bits);
        }
    } else if (kind == "delete") {
        if (!meta.DoesPropertyExist(ns, path))
            throw WorkerError("xmp_op_failed", "Свойство не найдено: " + t.path);
        meta.DeleteProperty(ns, path);
    } else if (kind == "appendItem") {
        XMP_OptionBits o = 0;
        if (!meta.GetProperty(ns, path, nullptr, &o) || !(o & kXMP_PropValueIsArray))
            throw WorkerError("xmp_op_failed", "Список не найден: " + t.path);
        meta.AppendArrayItem(ns, path, o & kXMP_PropArrayFormMask, op.value("value", "").c_str(),
                             op.value("uri", false) ? kXMP_PropValueIsURI : 0);
    } else if (kind == "insertItem") {
        XMP_OptionBits o = 0;
        if (!meta.GetProperty(ns, path, nullptr, &o) || !(o & kXMP_PropValueIsArray))
            throw WorkerError("xmp_op_failed", "Список не найден: " + t.path);
        meta.SetArrayItem(ns, path, op.at("index").get<int>(), op.value("value", "").c_str(), kXMP_InsertBeforeItem);
    } else if (kind == "setArray") {
        opSetArray(meta, t, op.at("form").get<std::string>(), op.at("items"));
    } else if (kind == "setLangAlt") {
        opSetLangAlt(meta, t, op.at("lang").get<std::string>(), op.at("value").get<std::string>());
    } else if (kind == "deleteLangAlt") {
        int idx = findLangItem(meta, t, op.at("lang").get<std::string>());
        if (idx == 0) throw WorkerError("xmp_op_failed", "Языковой вариант не найден в " + t.path);
        std::string item;
        SXMPUtils::ComposeArrayItemPath(ns, path, idx, &item);
        meta.DeleteProperty(ns, item.c_str());
    } else {
        throw WorkerError("bad_request", "Неизвестная операция XMP: " + kind);
    }
}

// Строгая проверка даты XMP (ISO 8601 по спецификации XMP): YYYY, YYYY-MM, YYYY-MM-DD,
// YYYY-MM-DDThh:mm[:ss[.s+]][TZD]. Отсутствующие части допустимы и не подставляются.
bool isValidXmpDate(const std::string& v) {
    auto digits = [&](size_t pos, size_t n, int& out) {
        if (pos + n > v.size()) return false;
        out = 0;
        for (size_t i = pos; i < pos + n; ++i) {
            if (v[i] < '0' || v[i] > '9') return false;
            out = out * 10 + (v[i] - '0');
        }
        return true;
    };
    int y, mo, d, h, mi, sec;
    if (!digits(0, 4, y) || y < 1) return false;
    if (v.size() == 4) return true;
    if (v[4] != '-' || !digits(5, 2, mo) || mo < 1 || mo > 12) return false;
    if (v.size() == 7) return true;
    if (v[7] != '-' || !digits(8, 2, d)) return false;
    static const int dim[] = {31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31};
    bool leap = (y % 4 == 0 && y % 100 != 0) || y % 400 == 0;
    int maxDay = dim[mo - 1] + ((mo == 2 && leap) ? 1 : 0);
    if (d < 1 || d > maxDay) return false;
    if (v.size() == 10) return true;
    if (v[10] != 'T' || !digits(11, 2, h) || h > 23 || v.size() < 16 || v[13] != ':' || !digits(14, 2, mi) || mi > 59)
        return false;
    size_t pos = 16;
    if (pos < v.size() && v[pos] == ':') {
        if (!digits(pos + 1, 2, sec) || sec > 59) return false;
        pos += 3;
        if (pos < v.size() && v[pos] == '.') {
            size_t start = ++pos;
            while (pos < v.size() && v[pos] >= '0' && v[pos] <= '9') ++pos;
            if (pos == start) return false;
        }
    }
    if (pos == v.size()) return true;  // время без часового пояса
    if (v[pos] == 'Z') return pos + 1 == v.size();
    if (v[pos] != '+' && v[pos] != '-') return false;
    int th, tm;
    if (!digits(pos + 1, 2, th) || th > 23 || pos + 3 >= v.size() || v[pos + 3] != ':' || !digits(pos + 4, 2, tm) || tm > 59)
        return false;
    return pos + 6 == v.size();
}

// Проверка значений известных схем (даты и логические значения).
static void validateKnown(Meta& meta) {
    struct Known { const char* ns; const char* name; char kind; };
    static const Known known[] = {
        {kXMP_NS_XMP, "CreateDate", 'd'},     {kXMP_NS_XMP, "ModifyDate", 'd'},
        {kXMP_NS_XMP, "MetadataDate", 'd'},   {kXMP_NS_PDF, "Trapped", 't'},
        {kXMP_NS_XMP_Rights, "Marked", 'b'},  {kXMP_NS_Photoshop, "DateCreated", 'd'},
    };
    for (const auto& k : known) {
        std::string v;
        XMP_OptionBits o = 0;
        if (!meta.GetProperty(k.ns, k.name, &v, &o) || !XMP_PropIsSimple(o)) continue;
        if (k.kind == 'd') {
            if (!isValidXmpDate(v))
                throw WorkerError("invalid_value", std::string("Некорректная дата в ") + k.name + ": " + v);
        } else if (k.kind == 'b' && v != "True" && v != "False") {
            throw WorkerError("invalid_value", std::string("Ожидается True или False в ") + k.name);
        } else if (k.kind == 't' && v != "True" && v != "False" && v != "Unknown") {
            throw WorkerError("invalid_value", "pdf:Trapped допускает только True, False или Unknown");
        }
    }
}

void XmpDoc::apply(const json& ops) {
    Meta work = impl_->meta.Clone();  // транзакция: при ошибке исходная модель не меняется
    size_t index = 0;
    try {
        for (const auto& op : ops) {
            applyOne(work, op);
            ++index;
        }
        validateKnown(work);
    } catch (const XMP_Error& e) {
        throw WorkerError("xmp_op_failed",
                          "Операция XMP #" + std::to_string(index + 1) + " не выполнена: " +
                              (e.GetErrMsg() ? e.GetErrMsg() : ""),
                          json{{"opIndex", index}});
    } catch (WorkerError& e) {
        e.details["opIndex"] = index;
        throw;
    }
    impl_->meta = work;
}

}  // namespace pm
