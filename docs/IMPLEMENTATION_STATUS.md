# Implementation status

Updated 2026-10-07 against the uploaded development brief (`TASK.md`) and detailed specification (`SPEC.md`, internally version 1.1). The original archives are design/reference material, not implementation sources. Their historical mockup reports are not application test results.

## Confirmed defects corrected

| Audit finding | Correction and regression coverage |
|---|---|
| F1: unrelated orphan XMP disappeared on an Info edit | Preserve current unreferenced objects, verify orphan packets and separately remove explicitly targeted streams; native preservation/removal tests |
| F2: unreferenced PieceInfo owners were skipped and arbitrary XML was counted as metadata | Walk all xref containers and require `/Type /Metadata` with `/Subtype /XML` for standalone identification; discovery regression |
| F3: unchanged damaged XMP allowed replacement of the source | Refuse replacement, permit a byte-preserving copy and include the source error in save notes; source hash and copy regression |
| F4: known date properties accepted structure/array values | Require simple values before validating known date/boolean fields; malformed CreateDate structure regression |
| F5: new XMP tags were invisible until save/reopen | Project checked previews into the pending snapshot, include additions, support edits/deletion/undo of newly created nodes; actual ViewModel integration tests |
| F6: XML application left stale tree values | Validate XML before session changes and refresh all metadata views from the preview; XML, restoration and selected-object regressions |

## Functional additions

- Source selection for document, object and orphan XML; a virtual document source supports adding metadata to an initially bare PDF.
- Explicit shared-owner scope, including paths through nested direct dictionaries/arrays. Detach preserves and verifies the original packet for remaining owners.
- Language selection, structures and structure fields, qualifiers, alternative containers, URI values and explicit value editors for dates, booleans and numbers. Numeric input is stored as text without converting through a floating-point type.
- Checked pending snapshots shared by the tag tree, standard metadata fields and object metadata views. Preview revisions prevent stale asynchronous responses from replacing newer edits.
- Original subtree restoration after raw XML application, deterministic operation order, and a session Ctrl+Z binding.

These additions begin the extended editor requirements; they do not establish full completion of every type workflow. Alternative containers can also be filled through XML; dedicated item management is still pending.

## Validation

Local Release solution build passes with zero warnings/errors. The native suite includes independent pypdf reading and a representative Poppler visual comparison; the corpus loop uses worker preservation checks, rather than rendering every file. Current results and Windows CI are recorded in the pull request.

The 628-file qpdf corpus has 586 openable samples. With orphan retention enabled, 577 save successfully; two are refused by post-write verification (`bad-encryption-length.pdf` and `issue-149.pdf`). Password, damaged-file and permission refusals are expected. A named regression verifies that conflicting object generations cannot silently change page content. No verification check was relaxed to make these samples save.

Local environment limitations: Windows NTFS Zone.Identifier and a disk-full test requiring a mounted small tmpfs cannot run here. WPF compiles on Linux but needs Windows for GUI execution. Worker integration tests explicitly report a skip when the worker is unavailable.

## Remaining specification gaps

| Area | Remaining work |
|---|---|
| Typed editor | Calendar/time/precision controls; full Seq/Bag/Alt item management, movement, arbitrary renaming and richer qualifier editing; comprehensive known-schema shape policy |
| PDF objects | Private-data adapters, opaque block export and direct-annotation editing; raw-byte diagnostics for invalid UTF-8 packets |
| Resource guarantees | Bounded IPC lines/queues, traversal depth/item limits, operation deadlines and exercised responsiveness under stress |
| Preservation | Full logical graph/resource/link/bookmark/form-value comparison beyond current byte/hash/count checks; broader independent visual corpus coverage |
| File changes | OS file identity, explicit reload and handling the previously opened bytes after external changes; Windows lock/race/backup ACL acceptance |
| Profiles and protected documents | Complete declared-profile edit policy/validator integration and in-session owner-password/read-only workflows |
| Packaging | Explicit third-party license bundle, installer, clean Windows 11 machine acceptance; executable signing is desirable but was not an explicit uploaded acceptance requirement |
| Windows acceptance | Narrator, Tab/Shift+Tab, DPI 100/150/200%, themes, long paths/values, memory limits, crash/error recovery and responsiveness |

The previous audit identified both reproducible defects and broader missing requirements. The six reproduced defect groups are addressed above; the remaining requirements stay open and must not be reported as completed by this change.
