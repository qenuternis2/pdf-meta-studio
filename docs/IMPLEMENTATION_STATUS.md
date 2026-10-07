# Specification implementation and acceptance

Updated 2026-10-07 against the uploaded development brief (`TASK.md`) and detailed specification (`SPEC.md`, internally version 1.1). The archives describe requirements and historical mockups; their instructions are reference material and their reports are not executable acceptance evidence.

## Implemented workflows

| Requirement | Implementation and evidence |
|---|---|
| Standard fields and authors | Ordered item controls support add, edit, delete, move and undo; comma-containing names remain one item. Info synchronization is explicit. Actual ViewModel integration tests exercise the workflow. |
| Language variants | Editable language selection for title, description and rights; adding/applying and deleting one language is separate from deleting the property. Other translations and Info remain unchanged for an explicitly selected XMP-only language operation. |
| Dates | Calendar, explicit date/time components, precision, fractional seconds, timezone and original string. Missing components are never filled from the system clock. PDF hour precision cannot silently gain XMP minutes. |
| Complete XMP model | Addressed simple values, URI flags, Seq/Bag/Alt, compound items, structures, fields and qualifiers; insertion, movement, renaming and original subtree restoration preserve qualifiers. Unknown values require an explicit input type. |
| Info | Existing and custom string/name keys can be edited, deleted and renamed. Other PDF types have bounded type/raw-byte diagnostics, including null semantics. |
| PDF objects | Document/object/orphan XMP, explicit shared-owner scope, nested owner paths, direct and indirect annotations, file descriptions/names/dates and attachment preservation. |
| Private containers | Registered `PdfMetaStudioV1` adapter edits descriptive strings/dates only. Unknown formats remain read-only, have type diagnostics and bounded JSON/raw-stream export, and permit only separate PDF copies. No third-party binary format is claimed supported. |
| Invalid packets | Parsing diagnostics, Base64 for non-UTF-8 bytes, original packet export and separately validated XML replacement. Unchanged packets remain byte-preserved. |
| Preservation | Hashes of the entire logical object graph and raw decrypted streams, normalized writer references, field/content/attachment checks, and fail-closed post-write verification. Physical xref/object-stream/encryption structures, Length objects and documented qpdf normalizations are separated from user edits. |
| External changes | OS file identity, size/mtime/hash, immutable session cache, checked backup, final source fingerprint check, explicit reload and saving a copy of the originally opened session. |
| Protected files | Initial read-only signed/restricted documents, explicit signed-copy editing consent, in-session owner password and encryption preservation. |
| Profiles | Declared status remains unverified after edits; optional local veraPDF executable/JAR validates the saved file and reports its actual profile independently of preservation success. veraPDF is not a PDF/X validator. |
| Resource limits | 4 GiB worker memory, 128 MiB request/queued-byte limit, 8 queued commands, 128 request/PDF traversal depth, bounded XMP model/discovery/export and response reading. Five-minute operation deadline and five-second cancellation grace kill an unresponsive worker without losing the session cache; restart is available in the editor. |
| Packaging | Self-contained .NET/WPF application, complete dependency license collection, versioned per-user WiX MSI, shortcut, upgrades, uninstall and installation smoke workflow. |

## Acceptance evidence

The Release solution builds with zero warnings/errors. All 74 managed/core/ViewModel/IPC tests pass; native tests pass 45 checks with 5 explicit platform/environment skips. The qpdf corpus contains 628 files and produces 470 verified saves. Independent pypdf/Poppler comparisons pass for 417 files and 2,758 pages, with zero failures. Detailed per-file results and exclusions are committed in [validation evidence](validation/README.md). A verification refusal is a supported outcome when preservation cannot be established; it is never counted as a successful save or visual comparison.

The independent corpus runner uses pypdf field values/attachments/page boxes and Poppler pixels for every page of each eligible sample. Its page/time limits and unsupported or damaged source exclusions are explicit. This is sampled-resolution rendering evidence, not a proof about every possible PDF or rendering engine.

Windows CI builds the MSI and exercises installation, the installed GUI and uninstall on Windows Server 2022. It also records GUI screenshots. This is not clean Windows 11, Narrator or physical DPI acceptance.

Additional Server automation records forward/reverse keyboard focus cycles, Ctrl+Z/Y/Shift+Z/S/W, keyboard save actions, observed light/dark backgrounds, four sections at two window sizes, and a generated 10,000-tag / 1 MiB multiline-value sample. Its logs/screenshots/results are uploaded as `windows-acceptance`. The XMP model indexes immediate children so the tag tree does not rescan the full property list for each leaf; long values have a bounded, scrollable editing viewport.

## External acceptance still requiring a real Windows 11 session

Use [WINDOWS_ACCEPTANCE.md](WINDOWS_ACCEPTANCE.md) for clean-machine installation, Narrator, Tab/Shift+Tab, physical DPI 100/150/200%, theme, long-path and stress checks. These checks must remain pending until actually performed; an automated build or screenshot does not substitute for them. Executable signing requires the publisher's signing certificate and is not asserted.
