#pragma once
#include "pdf_doc.hpp"

namespace pm {
QPDFObjectHandle privateEntry(QPDF& pdf, const std::string& address);
std::string privateAddress(const std::string& owner, const json& path, const std::string& application);
bool supportedPrivateEntry(QPDFObjectHandle entry);
json privateFields(QPDFObjectHandle entry);
json exportMetadata(const json& request, Context& context);
json exportPrivate(const json& request, Context& context);
}
