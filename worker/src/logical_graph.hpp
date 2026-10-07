#pragma once

#include "pdf_doc.hpp"
#include <map>

namespace pm {
using ReferenceMap = std::map<std::string, QPDFObjGen>;

// Bounded PDF type diagnostics, preserving binary strings and dictionary null semantics.
json pdfValueTree(QPDFObjectHandle value, bool resolveRoot = true, size_t depth = 0);

// Canonical fingerprints cover each logical object and raw decrypted stream content.
// References are normalized through the writer map; only documented writer structures are exempt.
json logicalGraph(QPDF& pdf, Context& context, const ReferenceMap* reverse = nullptr);
json checkLogicalGraph(QPDF& pdf, const json& expected, const ReferenceMap& renumber, Context& context);
}
