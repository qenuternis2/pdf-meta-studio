# Code audit — 2026-10-07

Baseline: `5e64f84df0c0092b8ac7030a6b8c0d7ab0489e79`. The working tree was clean before this audit; no user changes were overwritten. No `AGENTS.md` instructions were present in the repository or workspace ancestry.

## Scope and approach

Reviewed repository setup, architecture, dependency declarations, save/preview/validation paths, snapshot ownership, XAML bindings and resources, native worker helpers, and Windows build/GUI/MSI scripts. This is a focused maintenance audit, not a proof that every possible document or platform is covered.

The WPF/MVVM application targets .NET 10 and delegates PDF operations through the Core JSON-lines client to a C++20 worker using qpdf, XMP Toolkit and Expat. Native dependencies are pinned by the fetch script and vcpkg baseline; managed versions are centralized in `Directory.Packages.props`. Windows builds produce a self-contained x64 application and per-user WiX MSI. See [setup](../README.md#build-on-windows) and [Windows CI](../.github/workflows/windows.yml).

The maintenance plan was: establish a baseline, reproduce suspected defects, make small fixes with targeted regression checks, run the available checks again, then validate the actual Windows build through CI. Public interfaces and dependency versions remain unchanged. The owner explicitly approved displaying PDF type changes in the review report; ordinary same-type value changes keep their existing display format.

## Confirmed findings

P2 means a functional/performance or CI reliability defect; P3 means maintenance cleanup.

| Priority | Location | Cause and correction |
| --- | --- | --- |
| P2 | `src/PdfMetaStudio.Core/ProfileValidator.cs` | A bounded pipe reader failed on oversized output, but `Task.WhenAll` still waited for an unresponsive validator and its other pipe until the two-minute deadline. Cancel sibling reads and the exit wait when the existing limit is exceeded. Keep the same limit, error code, public API and saved-file semantics. |
| P2 | `src/PdfMetaStudio.Core/Editing/Review.cs`, `Model/Snapshot.cs` | Each changed XMP key searched the entire node list, making a large preview quadratic. Use the model's existing key index through an internal accessor, preserving paths, ordering, qualifiers and requested/automatic flags. |
| P2 | `src/PdfMetaStudio.Core/Editing/Review.cs` | `/Info` comparison discarded entry types, hiding Name-to-String changes with identical text. Compare kind and value and label both types only when the kind changes. This report behavior change was approved by the owner. |
| P2 | `scripts/prepare-windows-desktop.ps1` | A hosted setup process could exit between `Get-Process` and `Stop-Process`, aborting GUI/MSI preparation. Ignore only PowerShell's process-not-found error; access-denied and other errors still abort. The same race had occurred in Windows CI run `37650649624`, attempt 1. |
| P3 | `scripts/gui-smoke.ps1` | A desktop-guard branch after an earlier return was unreachable. Remove it and update the dialog-entry comment to match native ARM input and keyboard fallback. Preserve UTF-8 BOM for Windows PowerShell's Russian strings. |
| P3 | `docs/validation/README.md`, `scripts/build-windows.ps1` | Acceptance wording implied that all Windows 11 testing was pending despite successful hosted ARM64 CI. Distinguish automated ARM64 evidence from pending clean x64, offline/non-elevated, Narrator and physical DPI checks. |

The redundant `/Info` value conditional is subsumed by the kind-aware comparison. No dependency, public member, historical evidence file or product feature was removed. XAML event handlers/converters and CommunityToolkit-generated ViewModel code have dynamic or generated consumers and were retained. No tracked disposable archive, build binary or temporary log was confirmed to need removal.

## Verification

Before edits, the Release solution built with zero warnings/errors; all 75 managed tests passed with both real native worker and IPC test peer enabled. Native tests passed 46 checks with five explicit platform/environment skips. The 628-file qpdf corpus opened 586 files and completed 470 verified saves with zero unexpected failures. Seven files were refused by preservation verification; those are not successful saves. The .NET analyzer check passed.

Regression checks reproduced the validator defect on both stdout and stderr: each exceeded an eight-second external test cancellation bound before the fix; after the fix both returned the original output-limit error in under 0.1 seconds in the isolated local run. A 10,000-change XMP review retained all values and ordering; the same local test fell from 3.237 seconds to 0.166 seconds. These timings describe this run, not a cross-machine guarantee. Three type-change cases failed before the `/Info` fix. Desktop regression checks failed on the real PowerShell process-not-found error before the fix and now pass normal termination, already-exited and access-denied cases.

After edits:

- Release solution and native worker/test-tools builds pass; the solution has zero warnings/errors.
- All 84 managed/Core/ViewModel/IPC tests pass, with zero failures/skips and both worker executables enabled.
- Native suite: 46 passed, five explicit skips; corpus: 628 files, 586 opened, 470 verified saves, zero unexpected failures, same seven preservation refusals.
- `dotnet format analyzers PdfMetaStudio.sln --verify-no-changes --no-restore --severity warn` passes. No separate lint configuration is provided; no bulk formatting was applied.
- The 952 exported Core type/member signatures match the baseline assembly. The index accessor is internal; product worker protocol and application public declarations are untouched.
- PowerShell 7 syntax parsing passes for all ten scripts; the isolated desktop test passes all three cases. Windows CI also runs this regression in Windows PowerShell 5.1 before the normal packaging/GUI checks.
- Python AST parsing passes for five scripts; Bash syntax and `git diff --check` pass.
- NuGet vulnerability lookup reports no vulnerable direct/transitive packages from its configured source. Dependencies are unchanged; this is not an audit of SDKs or every native/tooling vulnerability database.

## Deferred work and limits

`src/PdfMetaStudio.Core/DocumentService.cs` retains immutable source snapshot directories until service disposal. Many opens/reloads in a long session can consume disk space (P2 follow-up). Earlier `DocumentSnapshot`/`EditSession` instances are public and may still need these files; deleting them on window close without an ownership policy could invalidate existing consumers. A separate lifetime design and owner agreement are needed before changing that behavior.

Clean-machine x64, non-elevated/offline installation, Narrator and physical 150/200% DPI acceptance remain pending in [Windows acceptance](WINDOWS_ACCEPTANCE.md). Linux build success does not establish WPF/MSI runtime behavior. The native PDF engine was not changed; historical independent rendering evidence was retained rather than relabeled as a new run. No authentic external veraPDF executable was available locally; validator parsing and bounded process handling are covered by controlled peers. Publisher signing still requires a signing certificate.
