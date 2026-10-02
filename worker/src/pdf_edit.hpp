#pragma once

#include "protocol.hpp"

namespace pm {

// Применить правки в памяти и вернуть результат «как станет» (без записи на диск).
json previewEdits(const json& request, Context& ctx);
// Применить правки, записать временный файл, проверить его и завершить сохранение.
json saveEdits(const json& request, Context& ctx);
// Открыть и описать документ.
json openDocument(const json& request, Context& ctx);

}  // namespace pm
