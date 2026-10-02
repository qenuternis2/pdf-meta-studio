// Полноценная модель XMP поверх Adobe XMP Core: разбор, выгрузка дерева
// (простые значения, Seq/Bag/Alt, языковые варианты, структуры, квалификаторы),
// адресные правки по пути из шагов с URI пространств имён, сериализация.
#pragma once

#include "protocol.hpp"

#include <cstddef>
#include <memory>
#include <string>

namespace pm {

void xmpInitialize();
void xmpTerminate();
std::string xmpToolkitVersion();

class XmpDoc {
public:
    XmpDoc();
    ~XmpDoc();
    XmpDoc(const XmpDoc&) = delete;
    XmpDoc& operator=(const XmpDoc&) = delete;

    // Разбор пакета; при ошибке — WorkerError("xmp_invalid").
    static std::unique_ptr<XmpDoc> parse(const std::string& packet, bool strict = true);
    static std::unique_ptr<XmpDoc> empty();

    // Дерево: {"namespaces":[{uri,prefix}], "nodes":[{...}]}.
    json model() const;
    // Применить список операций (см. docs/PROTOCOL.md, раздел «Операции XMP»).
    void apply(const json& ops);
    std::string serialize() const;
    // Значение простого свойства верхнего уровня (или x-default для Alt-text); пусто, если нет.
    bool getSimple(const std::string& ns, const std::string& name, std::string& out) const;

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

// Предварительная проверка пакета до XML-парсера: запрет DOCTYPE/ENTITY и лимит размера.
// Предел размера одного XMP-пакета (после распаковки потока).
inline constexpr size_t kMaxXmpPacketBytes = 64u * 1024u * 1024u;
// Предел суммарного объёма пакетов, которые worker отдаёт GUI в ответе на open.
inline constexpr size_t kMaxSnapshotPacketBytes = 256u * 1024u * 1024u;

void precheckPacket(const std::string& packet);
bool isValidXmpDate(const std::string& value);

}  // namespace pm
