# Validation evidence — 2026-10-07

Native PDF-processing implementation: `486fc386850eca8e600171d97a9b2dffcf91ba31`. Subsequent changes add Windows ACL/long-path acceptance, the worker's Windows long-path manifest and managed IPC recovery; the PDF-processing implementation used for these reports is unchanged.

Environment: Linux 6.18.44, .NET SDK 10.0.100, qpdf 12.4.2, Adobe XMP Toolkit 2025.03, Expat 2.7.1, pypdf 5.1.0, cryptography 46.0.3, Poppler 26.05.0.

| Check | Actual result |
|---|---|
| Solution compilation | Release build, zero warnings/errors |
| Managed/core/ViewModel/IPC tests | 73 passed, zero failures/skips, with both actual native worker and test IPC peer available |
| Native end-to-end tests | 45 passed, 5 explicit platform/environment skips |
| Native qpdf corpus | 628 files, 586 opened, 470 verified saves, zero unexpected failures |
| Independent corpus | 628 files classified, 417 independently compared successfully, zero failures |
| Complete-page pixel comparisons | 2,758 pages across those 417 files, 72 DPI capped at 1,024 pixels on the longest dimension |

[Native results](native-linux.json) include each end-to-end test, refusal categories and failed preservation checks. Seven corpus files were blocked by post-write verification; they are not successful saves. Three of these expose qpdf's documented RC4-to-AES normalization and are rejected because the application requires unchanged encryption methods.

[Independent results](independent-corpus-linux.json) list every corpus file and its actual outcome. The other 211 files comprise 23 password refusals, 10 preservation refusals, 111 damaged-structure refusals, 45 source parser/renderer exclusions, 7 page-limit exclusions, 5 permission refusals and 10 profile-policy refusals. A source exclusion means the independent tools could not reliably evaluate the original file, not that its output passed. All pages of every successful sample were compared; files over 50 pages and render operations exceeding 10 seconds have explicit limits.

The two runners apply different edit sets, so their save/refusal counts need not match. These reports use the public pinned qpdf corpus, not uploaded user PDFs. Rendering evidence is limited to the recorded engine and resolution.

Windows packaging, GUI automation and MSI installation/uninstallation results are linked from [implementation status](../IMPLEMENTATION_STATUS.md). Real Windows 11, Narrator and physical DPI checks remain pending in [Windows acceptance](../WINDOWS_ACCEPTANCE.md).
