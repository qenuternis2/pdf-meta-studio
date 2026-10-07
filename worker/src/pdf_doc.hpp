#pragma once

#include "fileutil.hpp"
#include "protocol.hpp"

#include <qpdf/QPDF.hh>
#include <qpdf/QPDFObjectHandle.hh>

#include <map>
#include <memory>
#include <set>
#include <string>
#include <vector>

namespace pm {

struct LoadedPdf {
    std::unique_ptr<QPDF> q;
    fs::path path;
    std::vector<std::string> warnings;
};

// Открыть PDF только для чтения. Пароль не логируется.
LoadedPdf openPdf(const fs::path& path, const std::string& password, Context* ctx);

std::string refOf(QPDFObjGen og);
QPDFObjGen parseRef(const std::string& ref);

// Владелец /Metadata-потока (объект, в словаре которого есть ключ /Metadata).
struct MetaOwner {
    QPDFObjGen og;           // ближайший косвенный объект
    std::string kind;        // catalog, page, image, form, annotation, filespec, embeddedFile, font, object
    std::string label;       // человекочитаемая подпись
    std::string keyPath;     // путь к словарю внутри объекта (пусто — сам объект)
    json path = json::array(); // Dictionary keys and array indexes, without string parsing.
    int pageIndex = -1;
    json toJson() const;
};

struct MetaStream {
    QPDFObjectHandle stream;
    QPDFObjGen og;
    std::vector<MetaOwner> owners;
    bool reachable = true;
};

struct ScanIssue {
    std::string area;
    std::string reason;
};

struct Discovery {
    std::vector<MetaStream> streams;
    std::vector<json> pieceInfo;
    std::vector<ScanIssue> issues;
    size_t reachableObjects = 0;
    size_t totalObjects = 0;
    MetaStream* find(QPDFObjGen og);
};

Discovery discover(QPDF& q, Context* ctx);

// Полный снимок документа для GUI.
json inspect(LoadedPdf& pdf, Context& ctx);
json infoToJson(QPDF& q);
json signatureInfo(QPDF& q);
json encryptionInfo(QPDF& q);
// Снимок неизменяемых частей для проверки после записи.
json structureSnapshot(QPDF& q);
std::string streamBytes(QPDFObjectHandle stream);

}  // namespace pm
