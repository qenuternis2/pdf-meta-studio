# PDF Meta Studio

A local PDF metadata editor for Windows 11 x64. The interface uses C# WPF, MVVM and .NET 10. A C++ worker uses qpdf and Adobe XMP Core to read and write PDF `/Info`, document XMP and object XMP over a [JSON Lines protocol](docs/PROTOCOL.md).

The application provides typed metadata editing, review, verified save-copy/replacement and recovery workflows. See [specification implementation and acceptance](docs/IMPLEMENTATION_STATUS.md) for the current scope and validation evidence. The application interface remains Russian; repository documentation and new development work use English.

## Repository structure

| Path | Purpose |
|---|---|
| `worker/` | C++20 PDF/XMP engine, native dependency configuration, end-to-end tests and PDF generator |
| `src/PdfMetaStudio.Core/` | Worker client, snapshots, date codecs, edit sessions, undo/redo, Info/XMP synchronization and review |
| `src/PdfMetaStudio.App/` | WPF application, published as `PdfMetaStudio.exe` |
| `tests/PdfMetaStudio.Core.Tests/` | Core, worker integration and actual editor ViewModel tests |
| `scripts/deps.lock` | qpdf v12.4.2, XMP Toolkit v2025.03 and Expat 2.9.0 tags and commit hashes |
| `scripts/patches/xmp-keep-translations.patch` | Required XMP SDK patch preventing translation loss |
| `Directory.Packages.props`, `global.json`, `worker/vcpkg.json` | Pinned SDK, NuGet versions and native dependency baseline |

## Build on Windows

Install Visual Studio 2022 or Build Tools with MSVC, CMake ≥ 3.21, Git, the .NET SDK pinned in `global.json` (10.0.112), Python ≥ 3.9 and vcpkg. Set `VCPKG_ROOT` and use Developer PowerShell for VS 2022:

```powershell
pip install pypdf==6.19.0 cryptography==50.0.2
./scripts/build-windows.ps1
```

The script fetches and verifies pinned dependencies, builds the worker and qpdf test tools, runs native and .NET tests, and publishes a self-contained application to `dist/PdfMetaStudio`. No separate .NET installation is needed on the target machine. The build also collects complete third-party licenses, includes executed test reports under `validation`, and produces a per-user MSI in `dist/installer`. A build with `-SkipTests` excludes test reports.

[Windows CI](.github/workflows/windows.yml) runs this build and a GUI UI Automation smoke test, uploads the application and screenshots, exercises MSI installation/uninstallation, and publishes release ZIPs/MSIs and checksums for version tags.

## Validate on Linux

Install a C++20 compiler, CMake, Ninja, zlib/libjpeg development packages, the pinned .NET SDK and Python. `pypdf==6.19.0` with `cryptography==50.0.2` and Poppler's `pdftoppm` enable independent parsing and a representative visual comparison.

```bash
./scripts/fetch-deps.sh
cmake -S worker -B build-worker -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build-worker --parallel --target pdfmeta-worker test-tools
export PDFMETA_WORKER="$PWD/build-worker/pdfmeta-worker"
export PDFMETA_TEST_WORKER="$PWD/build-worker/pdfmeta-test-worker"
export QPDF_CLI="$PWD/build-worker/qpdf/qpdf/qpdf"
python3 worker/tests/run_tests.py "$PDFMETA_WORKER" --corpus worker/external/qpdf/qpdf/qtest/qpdf

dotnet test tests/PdfMetaStudio.Core.Tests -c Release
dotnet build -c Release
```

Building the WPF project on Linux verifies compilation, not GUI execution. Worker-dependent .NET tests are explicitly skipped if `PDFMETA_WORKER` is unavailable; IPC fault/deadline tests require `PDFMETA_TEST_WORKER`. Tests using native fixtures must run with the built workers to provide integration evidence.

## Preservation and editing behavior

- All current xref objects are retained by default, including unrelated orphan metadata. Discovery scans both the reachable graph and unreferenced containers. Generic XML streams are not automatically classified as metadata.
- Shared XMP requires an explicit choice: edit all owners or detach a packet for one owner. Structured owner paths support nested direct dictionaries and arrays.
- Applying edits produces a checked pending snapshot. Added properties appear immediately; XML application updates the tree, standard fields and object views. XML is available for document, object and orphan packets.
- Advanced creation supports text, dates, booleans, explicit numbers, URI values, Seq/Bag, language variants, structures, alternatives and qualifiers. Language choices preserve other translations. Unknown properties remain text until the user explicitly selects a type.
- Invalid XML is rejected before replacing pending operations. Undo/redo operate on the edit session; restoring a property after XML replacement copies its original subtree, including qualifiers.
- Damaged XMP may be replaced with validated XML. An unchanged damaged packet permits saving only a separate copy; its original bytes are preserved and reported.
- Saving writes a temporary file, reopens it and checks metadata, page content, annotation content, bookmark/form counts, attachments and encryption before committing. Replacement verifies a backup and rechecks the source fingerprint.
- Full rewriting removes historical incremental revisions and recreates object numbering/xref offsets. Unrelated unreferenced objects are preserved; an explicitly removed metadata stream is discarded only if no current reference remains.
- The XMP SDK patch prevents `x-default` from silently replacing another translation. Modified packets are serialized, reparsed and compared semantically. DTDs and entity declarations are prohibited. Decompression caps protect XMP processing; Windows also uses a 4 GiB worker Job limit.

A save refusal is a supported result when preservation cannot be established. For example, the damaged qpdf `issue-149.pdf` contains conflicting object generations: retaining unreferenced objects causes the upstream writer to select different page content. The post-write check rejects that result and leaves the source unchanged.

See the [security audit](docs/SECURITY_AUDIT.md) for verified findings, dependency advisory applicability, trust boundaries and remaining Windows isolation checks. The worker is a separate process with resource limits; it is not an OS security sandbox.

## Independent corpus and Windows acceptance

```bash
python3 worker/tests/independent_corpus.py "$PDFMETA_WORKER" worker/external/qpdf/qpdf/qtest/qpdf --report independent-corpus.json
```

This additionally requires pypdf and Poppler, compares every page of each eligible sample, and explicitly reports parser/renderer exclusions, page/time limits and writer refusals. Rendered resolution is recorded in the report.

[Windows 11 acceptance](docs/WINDOWS_ACCEPTANCE.md) records the remaining physical-machine/Narrator/DPI checks. Windows Server CI results do not establish those manual results. Optional veraPDF CLI validation is local and separate from save success; the selected executable or executable JAR must accept `--format xml <file>` and produce a veraPDF XML report. Java is required for a JAR. PDF/X stays unverified unless independently checked by a matching validator.
