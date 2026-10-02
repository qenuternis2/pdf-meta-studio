#include "object_fields.hpp"

#include "pdf_doc.hpp"
#include "sha256.hpp"

#include <qpdf/QPDFEFStreamObjectHelper.hh>
#include <qpdf/QPDFEmbeddedFileDocumentHelper.hh>
#include <qpdf/QPDFFileSpecObjectHelper.hh>
#include <qpdf/QPDFPageDocumentHelper.hh>
#include <qpdf/QPDFPageObjectHelper.hh>

namespace pm {

namespace {

const ObjectField kFields[] = {
    {"annotation", "author", "/T", "Автор", false, true},
    {"annotation", "subject", "/Subj", "Тема", false, true},
    {"annotation", "modified", "/M", "Дата изменения", true, true},
    {"annotation", "created", "/CreationDate", "Дата создания", true, true},
    {"attachment", "filename", "/UF", "Имя файла", false, false},
    {"attachment", "description", "/Desc", "Описание", false, true},
    {"attachment", "created", "/Params /CreationDate", "Дата создания", true, true},
    {"attachment", "modified", "/Params /ModDate", "Дата изменения", true, true},
};

bool digits(const std::string& v, size_t pos, size_t n, int& out) {
    if (pos + n > v.size()) return false;
    out = 0;
    for (size_t i = pos; i < pos + n; ++i) {
        if (v[i] < '0' || v[i] > '9') return false;
        out = out * 10 + (v[i] - '0');
    }
    return true;
}

bool isAscii(const std::string& s) {
    for (unsigned char c : s)
        if (c < 0x20 || c > 0x7e) return false;
    return true;
}

json stringOrNull(QPDFObjectHandle o) {
    if (o.isString()) return sanitizeUtf8(o.getUTF8Value());
    return nullptr;
}

std::shared_ptr<QPDFFileSpecObjectHelper> resolveAttachment(QPDF& q, const std::string& name) {
    QPDFEmbeddedFileDocumentHelper ef(q);
    auto spec = ef.getEmbeddedFile(name);
    if (!spec) throw WorkerError("bad_request", "Вложение не найдено: " + name);
    return spec;
}

QPDFObjectHandle efParams(QPDFFileSpecObjectHelper& spec, bool create) {
    QPDFObjectHandle s = spec.getEmbeddedFileStream();
    if (!s.isStream()) throw WorkerError("unsupported", "У вложения нет потока файла: даты хранить негде");
    QPDFObjectHandle dict = s.getDict();
    QPDFObjectHandle params = dict.getKey("/Params");
    if (!params.isDictionary() && create) {
        params = QPDFObjectHandle::newDictionary();
        dict.replaceKey("/Params", params);
    }
    return params;
}

}  // namespace

bool isValidPdfDate(const std::string& v) {
    if (v.size() < 6 || v.compare(0, 2, "D:") != 0) return false;
    int year = 0, month = 1, day = 1, hour = 0, minute = 0, second = 0;
    size_t p = 2;
    if (!digits(v, p, 4, year)) return false;
    p += 4;
    auto next2 = [&](int& out) {
        if (p + 2 <= v.size() && v[p] >= '0' && v[p] <= '9') {
            if (!digits(v, p, 2, out)) return false;
            p += 2;
            return true;
        }
        return false;
    };
    if (next2(month) && next2(day) && next2(hour) && next2(minute)) next2(second);
    if (month < 1 || month > 12 || hour > 23 || minute > 59 || second > 59) return false;
    static const int dim[] = {31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31};
    bool leap = (year % 4 == 0 && year % 100 != 0) || year % 400 == 0;
    int maxDay = dim[month - 1] + (month == 2 && leap ? 1 : 0);
    if (day < 1 || day > maxDay) return false;
    if (p == v.size()) return true;
    char tz = v[p++];
    if (tz == 'Z') {
        // После Z старые программы иногда пишут нулевое смещение «00'00'».
        std::string rest = v.substr(p);
        return rest.empty() || rest == "00'00'" || rest == "00'00" || rest == "00'" || rest == "00";
    }
    if (tz != '+' && tz != '-') return false;
    int oh = 0, om = 0;
    if (!digits(v, p, 2, oh) || oh > 23) return false;
    p += 2;
    if (p == v.size()) return true;
    if (v[p] == '\'') ++p;
    if (p == v.size()) return true;
    if (!digits(v, p, 2, om) || om > 59) return false;
    p += 2;
    if (p < v.size() && v[p] == '\'') ++p;
    return p == v.size();
}

const ObjectField* findObjectField(const std::string& kind, const std::string& field) {
    for (const auto& f : kFields)
        if (kind == f.kind && field == f.field) return &f;
    return nullptr;
}

QPDFObjectHandle resolveAnnotation(QPDF& q, const std::string& ref, std::string* label) {
    QPDFObjGen og = parseRef(ref);
    auto pages = q.getAllPages();
    for (size_t i = 0; i < pages.size(); ++i) {
        for (auto& a : QPDFPageObjectHelper(pages[i]).getAnnotations()) {
            QPDFObjectHandle o = a.getObjectHandle();
            if (o.isIndirect() && o.getObjGen() == og) {
                if (label) {
                    std::string st = sanitizeUtf8(a.getSubtype());
                    if (!st.empty() && st[0] == '/') st.erase(0, 1);
                    *label = "Аннотация " + st + " на странице " + std::to_string(i + 1);
                }
                return o;
            }
        }
    }
    throw WorkerError("bad_request", "Аннотация не найдена на страницах документа: " + ref);
}

std::string objectLabel(QPDF& q, const std::string& kind, const std::string& address) {
    if (kind == "annotation") {
        std::string label;
        resolveAnnotation(q, address, &label);
        return label;
    }
    auto spec = resolveAttachment(q, address);
    std::string fn = sanitizeUtf8(spec->getFilename());
    return "Вложение " + (fn.empty() ? sanitizeUtf8(address) : fn);
}

json readObjectField(QPDF& q, const std::string& kind, const std::string& address, const ObjectField& f) {
    if (kind == "annotation") return stringOrNull(resolveAnnotation(q, address).getKey(f.key));
    auto spec = resolveAttachment(q, address);
    std::string field = f.field;
    if (field == "filename") {
        QPDFObjectHandle so = spec->getObjectHandle();
        QPDFObjectHandle uf = so.getKey("/UF");
        return uf.isString() ? stringOrNull(uf) : stringOrNull(so.getKey("/F"));
    }
    if (field == "description") return stringOrNull(spec->getObjectHandle().getKey("/Desc"));
    QPDFObjectHandle s = spec->getEmbeddedFileStream();
    if (!s.isStream()) return nullptr;
    QPDFObjectHandle params = s.getDict().getKey("/Params");
    if (!params.isDictionary()) return nullptr;
    return stringOrNull(params.getKey(field == "created" ? "/CreationDate" : "/ModDate"));
}

void writeObjectField(QPDF& q, const std::string& kind, const std::string& address, const ObjectField& f,
                      const json& value, std::vector<std::string>& notes) {
    if (!value.is_null()) {
        if (!value.is_string()) throw WorkerError("bad_request", "Значение поля должно быть строкой");
        if (f.isDate && !isValidPdfDate(value.get<std::string>()))
            throw WorkerError("invalid_value", std::string(f.label) + ": некорректная дата PDF «" +
                                                   sanitizeUtf8(value.get<std::string>()) + "»");
    } else if (!f.deletable) {
        throw WorkerError("bad_request", std::string(f.label) + " нельзя удалить");
    }

    if (kind == "annotation") {
        QPDFObjectHandle o = resolveAnnotation(q, address);
        if (value.is_null()) {
            o.removeKey(f.key);
        } else {
            const std::string v = value.get<std::string>();
            // Даты — строки PDF из ASCII; текст — строка Unicode.
            o.replaceKey(f.key, f.isDate ? QPDFObjectHandle::newString(v) : QPDFObjectHandle::newUnicodeString(v));
        }
        return;
    }

    auto spec = resolveAttachment(q, address);
    std::string field = f.field;
    QPDFObjectHandle so = spec->getObjectHandle();
    if (field == "filename") {
        const std::string v = value.get<std::string>();
        if (v.empty()) throw WorkerError("invalid_value", "Имя файла вложения не может быть пустым");
        so.replaceKey("/UF", QPDFObjectHandle::newUnicodeString(v));
        if (isAscii(v)) {
            so.replaceKey("/F", QPDFObjectHandle::newString(v));
        } else if (so.hasKey("/F")) {
            notes.push_back("Имя вложения записано в /UF; прежнее /F для старых программ оставлено без изменений");
        }
        return;
    }
    if (field == "description") {
        if (value.is_null()) so.removeKey("/Desc");
        else so.replaceKey("/Desc", QPDFObjectHandle::newUnicodeString(value.get<std::string>()));
        return;
    }
    const char* pkey = field == "created" ? "/CreationDate" : "/ModDate";
    QPDFObjectHandle params = efParams(*spec, !value.is_null());
    if (value.is_null()) {
        if (params.isDictionary()) params.removeKey(pkey);
    } else {
        params.replaceKey(pkey, QPDFObjectHandle::newString(value.get<std::string>()));
    }
}

json annotationContentsHashes(QPDF& q) {
    json pages = json::array();
    for (auto& p : q.getAllPages()) {
        json list = json::array();
        for (auto& a : QPDFPageObjectHelper(p).getAnnotations()) {
            QPDFObjectHandle c = a.getObjectHandle().getKey("/Contents");
            if (!c.isString()) {
                list.push_back(nullptr);
                continue;
            }
            std::string s = c.getStringValue();
            Sha256 h;
            h.update(s.data(), s.size());
            list.push_back(h.hexDigest());
        }
        pages.push_back(list);
    }
    return pages;
}

}  // namespace pm
