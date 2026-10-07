# GUI ↔ worker protocol

The GUI starts `pdfmeta-worker.exe` without elevation. Transport is UTF-8 JSON Lines over stdin/stdout. Passwords appear only in request bodies, never in command-line arguments or logs. After startup the worker emits `{"type":"ready","protocol":1}`.

Requests use unique integer `id` values. Commands run serially; a separate reader handles cancellation immediately.

| Command | Inputs and behavior |
|---|---|
| `hello` | Worker, qpdf and XMP versions |
| `open` | `path`, optional `password`; read-only snapshot |
| `validateXmp` | `xml`; parse and validate known values without opening/writing a PDF; return `model` and serialized `packet` |
| `preview` | `path`, optional `password`, `edits`; apply in memory and return before/after metadata |
| `save` | `path`, optional `password`, source fingerprint `expect`, `edits`, `mode` (`copy`/`replace`), copy `target`, `options.allowSignedCopy` |
| `exportMetadata` | `path`, optional `snapshotPath`/`password`, `stream`, `target`; export original decoded packet bytes without UTF-8 conversion |
| `exportPrivate` | `path`, optional `snapshotPath`/`password`, discovered private `address`, `target`; bounded typed JSON plus raw referenced stream bytes |
| `cancel` | `target`: request id |
| `shutdown` | Stop the worker |

```json
{"id":7,"cmd":"open","path":"C:\\Docs\\example.pdf"}
{"id":7,"type":"progress","stage":"inspect","percent":40}
{"id":7,"type":"result","data":{}}
{"id":7,"type":"error","code":"file_locked","message":"...","details":{}}
```

Progress stages include `hash`, `open`, `inspect`, `check`, `apply`, `write`, `verify` and `commit`.

## Edits

```json
{
  "info": [{"op":"set","key":"/Title","value":"...","type":"string"},
           {"op":"delete","key":"/Custom"}],
  "xmp": [{"stream":"12 0","scope":"detach","owner":"5 0","ownerPath":["/Nested",0],
           "action":"edit","ops":[]}],
  "objects": [{"kind":"annotation","address":"7 0","field":"author","op":"set","value":"..."},
              {"kind":"attachment","address":"data.bin","field":"modified","op":"delete"}]
}
```

Info values support `string` and `name`. `stream: null` creates a packet for `owner` (`catalog` or an object reference). Shared packets require `scope: all` or `detach`. Detach selects exactly one owner using its reference and structured `ownerPath`: dictionary keys and zero-based array indices from the containing indirect object to the direct owner dictionary. Use the path returned by discovery; a missing path means `[]`. Owner discovery also retains a display `keyPath`.

`action: remove` removes Metadata links from selected owners. The target stream is discarded only when no current reference remains. Unrelated orphan objects and packets are retained.

XMP paths use namespace URIs rather than prefixes:

```json
[{"t":"prop","ns":"https://example.org/ns/","name":"Record"},
 {"t":"field","ns":"https://example.org/ns/","name":"Label"},
 {"t":"qual","ns":"https://example.org/ns/","name":"Source"}]
```

Array items use `{"t":"item","i":1}` (one-based).

| XMP operation | Behavior |
|---|---|
| `set` | Change a simple value; preserve its URI flag unless explicit `uri` is supplied |
| `create` | Create `simple`, `seq`, `bag`, `alt`, `altText` or `struct`; simple values use `value`/optional `uri`; qualifier paths create simple qualifiers on existing parents |
| `delete` | Remove an addressed node |
| `restore` | Copy the addressed subtree from original packet `xml`, retaining structure and qualifiers |
| `appendItem`, `insertItem` | Add array `value`; insertion uses one-based `index` |
| `setArray` | Update Seq/Bag using `form` and string `items`; retained positions preserve qualifiers |
| `setLangAlt`, `deleteLangAlt` | Set/delete language `lang`; setting uses `value`, and preserves other translations |
| `replacePacket` | Replace the complete packet with `xml` |

Operations apply transactionally to a cloned XMP model, then validate known date/boolean fields. Serialization is reparsed and compared semantically. XMP decompression is capped at 64 MiB per packet; total packet bytes in `open` are capped at 256 MiB. Oversized packets report `xmp_too_large`; complete replacement/removal can still be requested. DTD and entity declarations are rejected.

## Annotation and attachment fields

Indirect annotation dictionaries are addressed by `ref` from `open`; direct annotations use the supplied `page:N:annot:M` address. Attachments use their EmbeddedFiles tree `name`. A `null` current field value means absent.

| Kind | Field | PDF keys | Deletable |
|---|---|---|---|
| `annotation` | `author`, `subject` | `/T`, `/Subj` | Yes |
| `annotation` | `modified`, `created` | `/M`, `/CreationDate` | Yes |
| `attachment` | `filename` | `/UF`, also `/F` for ASCII names | No |
| `attachment` | `description` | `/Desc` | Yes |
| `attachment` | `created`, `modified` | `/Params /CreationDate`, `/Params /ModDate` | Yes |

PDF dates use strict calendar validation: `D:YYYY[MM[DD[HH[mm[SS]]]]]` with an optional zone. Annotation Contents and attachment bytes cannot be edited and are checked after writing. Preview object entries report `kind`, `address`, `label`, `field`, `key`, `fieldLabel`, `before` and `after`.

## Save checks and errors

Save returns `checks`, `writer`, and `changes`. The writer reports rewritten versions/numbering, preserved unreferenced objects and incremental history removal. Metadata notes include preserved source XMP errors; the GUI shows these alongside writer notes.

| Errors | Meaning |
|---|---|
| `password_required`, `password_incorrect`, `unsupported_encryption`, `permission_denied` | Password/encryption/modification restrictions |
| `pdf_damaged`, `pdf_open_failed`, `unsupported` | Unsupported or unreadable source |
| `file_not_found`, `file_locked`, `access_denied`, `io_error`, `no_space`, `write_failed` | File/write failures |
| `external_change`, `target_is_source` | Source fingerprint changed or copy points to source |
| `signed_document`, `private_data_copy_only`, `xmp_copy_only` | Separate-copy policy; signed copies additionally require acknowledgement |
| `no_changes`, `scope_required`, `bad_request` | Missing changes/scope or invalid request |
| `xmp_invalid`, `xmp_forbidden_dtd`, `xmp_too_large`, `xmp_source_invalid`, `xmp_op_failed`, `invalid_value`, `xmp_roundtrip_mismatch` | XMP parse/edit/validation failures |
| `verification_unavailable`, `verification_failed` | Preservation cannot be established; failed checks appear in `details.checks` |
| `cancelled`, `out_of_memory`, `internal` | Cancellation/resource/internal errors |

Save errors leave the source unchanged and remove temporary files. Unchanged damaged XMP is copied byte-for-byte only in separate-copy mode; replacement requires fixing or removing the damaged packet first.

## Limits and session snapshots

Requests are at most 128 MiB and 128 JSON levels; the pending queue holds at most eight requests and 128 MiB in total. Invalid envelopes do not terminate the worker. Queued cancellation removes the pending command; active cancellation is cooperative. Native operation deadlines are five minutes; the GUI kills an unresponsive worker after the deadline or five seconds after cancellation and can restart it while retaining the opened session bytes.

`open.file.fingerprint.identity` is the OS device/inode or Windows volume/file identity. The GUI caches the opened bytes in a private session directory and supplies `snapshotPath` for preview/save/export. Replacement always checks the original fingerprint before work and before commit; copy can use a verified snapshot after external source changes. A snapshot may never be an export/copy target.

XMP operations additionally include `moveItem` (`from`/`to`, one-based), `rename` (`toSteps`, same parent/step kind), and compound `appendItem`/`insertItem` (`form`, optional `uri`, `index`). Structural/indexed operations are ordered history entries, rather than last-value replacements. `xml:lang` is a system qualifier, not a renameable field.

Private addresses are the opaque JSON strings supplied by discovery. Only the registered `/Private /Schema /PdfMetaStudioV1` dictionary has a write adapter (`label`, `description`, `modified`). All other schemas are opaque; exact raw export does not certify third-party compatibility.
