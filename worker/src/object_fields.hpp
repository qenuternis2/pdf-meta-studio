#pragma once

// Метаданные аннотаций и вложений: автор, тема и даты аннотации; имя файла, описание и даты вложения.
// Текст комментария (/Contents) и байты вложенного файла не изменяются никогда.

#include "protocol.hpp"

#include <qpdf/QPDF.hh>

#include <string>

namespace pm {

// Строгая проверка даты PDF: D:YYYY[MM[DD[HH[mm[SS]]]]][Z|+HH'mm'|-HH'mm'], без подстановки частей.
bool isValidPdfDate(const std::string& v);

struct ObjectField {
    const char* kind;    // annotation | attachment
    const char* field;   // имя поля в протоколе
    const char* key;     // ключ PDF (для отчёта)
    const char* label;   // подпись по-русски
    bool isDate;
    bool deletable;
};

// Поле по виду объекта и имени; nullptr, если такого поля нет (например, /Contents).
const ObjectField* findObjectField(const std::string& kind, const std::string& field);

// Адрес объекта: аннотация — ссылка "N G" на её словарь, вложение — ключ в дереве /EmbeddedFiles.
// Объект ищется только среди аннотаций страниц и вложений документа.
QPDFObjectHandle resolveAnnotation(QPDF& q, const std::string& ref, std::string* label = nullptr);
json readObjectField(QPDF& q, const std::string& kind, const std::string& address, const ObjectField& f);
void writeObjectField(QPDF& q, const std::string& kind, const std::string& address, const ObjectField& f,
                      const json& value, std::vector<std::string>& notes);
std::string objectLabel(QPDF& q, const std::string& kind, const std::string& address);

// Хеши /Contents всех аннотаций по страницам — для проверки, что текст комментариев не изменился.
json annotationContentsHashes(QPDF& q);

}  // namespace pm
