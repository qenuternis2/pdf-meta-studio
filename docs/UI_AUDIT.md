# Windows UI audit and corrections — 2026-10-10

The findings and line numbers below describe the original audit source
`bb039c19ea53b7f935b76801068e8d3ebf3af375`. The user subsequently authorized
corrections in [PR #22](https://github.com/qenuternis2/pdf-meta-studio/pull/22).
Original failing evidence is retained; final correction evidence is recorded below.

## src/PdfMetaStudio.App/ViewModels/TagTreeViewModel.cs

- src/PdfMetaStudio.App/ViewModels/TagTreeViewModel.cs:182 — **P1:** searching rebuilds the same selected path and overwrites its unapplied `EditValue`; preserve the draft before replacing the selected node.
- src/PdfMetaStudio.App/ViewModels/TagTreeViewModel.cs:136 — **P1:** switching XMP streams clears `_rawDirty` and overwrites unapplied XML without confirmation; retain per-stream drafts or confirm their discard.
- src/PdfMetaStudio.App/ViewModels/TagTreeViewModel.cs:218 — **P3:** rebuilding resets collapsed roots to expanded; preserve expansion by stable node path.

## src/PdfMetaStudio.App/ViewModels/EditorViewModel.cs

- src/PdfMetaStudio.App/ViewModels/EditorViewModel.cs:341 — **P1:** closing checks only applied session changes; an unapplied XML draft permits closing with no unsaved question. Include drafts in the close guard without treating them as already valid/applied edits.

## src/PdfMetaStudio.App/Views/DateEditorView.xaml

- src/PdfMetaStudio.App/Views/DateEditorView.xaml:23 — **P2:** applying an invalid year produces an error but does not focus the invalid input; focus the first failing component.

## src/PdfMetaStudio.App/Views/EditorView.xaml

- src/PdfMetaStudio.App/Views/EditorView.xaml:181 — **P2:** the read-only page-dimensions TextBox has an empty UI Automation name; provide a stable purpose label.
- src/PdfMetaStudio.App/Views/EditorView.xaml:357 — **P2:** expanded XML clips its apply action at a 640×480 window in both themes; make the entire XML/action region scrollable. At a simulated 2× layout transform, the close action also leaves the viewport.
- src/PdfMetaStudio.App/Views/EditorView.xaml:463 — **P2:** both review scope radios remain unchecked when the model selects `all`; bind their displayed selection to the current scope.
- src/PdfMetaStudio.App/Views/EditorView.xaml:466 — **P2:** all 1,000 review containers are realized; layout takes 4,995 ms in light and 4,920 ms in dark. Use a compatible virtualizing scroll owner/panel and verify keyboard/UIA behavior; full realization alone does not prove a hang.
- src/PdfMetaStudio.App/Views/EditorView.xaml:532 — **P2:** a status update emits zero UIA live-region events despite `LiveSetting="Polite"`; the explicit-event positive control delivers two events. Raise the appropriate peer notification when status changes; Narrator speech remains untested.

## Original checks and evidence

[Machine-readable results](validation/ui-audit-windows11.json) preserve source commits,
host information, native provenance, every additional check and baseline results.
Application source, native source, installer and dependency inputs were unchanged
between the original baseline and original additional audit; only automation and
documentation changed. These results describe the behavior before PR #22.

| Run | Source commit | Actual result |
|---|---|---|
| [Full Windows workflow](https://github.com/qenuternis2/pdf-meta-studio/actions/runs/38049028633) | `cd60b7fee210bf2b44b5532cfecca95c37245c8a` | Success: native/managed build, 85 Core tests, 50 Windows 11 native/file checks, GUI acceptance and MSI install/upgrade/uninstall |
| [Baseline GUI rerun](https://github.com/qenuternis2/pdf-meta-studio/actions/runs/38050156918) | `31b1a3c1fc548e8646e3fd68c918f4fdc3112f34` | Three production GUI scenarios passed; the separate additional harness failed to initialize and is excluded |
| [Final additional UI audit](https://github.com/qenuternis2/pdf-meta-studio/actions/runs/38051702749) | `bb039c19ea53b7f935b76801068e8d3ebf3af375` | 24 FAIL, 4 PASS, 4 SKIP, **zero ERROR/BLOCKED**; workflow failure correctly preserves application findings |

The final run explicitly used `additional_only=true`: it rebuilt the managed app
and diagnostic executable, verified unchanged native inputs against successful
package run `37729944353`, and reused that native worker. It did **not** repeat the
baseline GUI/native/MSI acceptance. The 24 FAIL records repeat 12 scenarios in two
themes; they are not 24 distinct defects. Both processes completed normally with
exit code 1 (42.22/37.45 seconds), wrote all results, disposed the worker and restored
theme/high-contrast state. Synthetic PDFs contain two Info keys and two XMP streams;
no user documents were used.

The full workflow's native corpus checked 628 files, opened 586 and verified 470
saves with zero unexpected failures. Seven files were blocked by preservation
verification; refusals and parser exclusions are not successful saves. Three
Windows native skips concern Linux-only disk-space/POSIX/chmod conditions.
The MSI upgrade from 0.3.0 to 0.3.1 verified 424 payload files and preserved both
control documents. GUI acceptance covered light/dark, keyboard shortcuts/cycles,
calendars, resizing and 10,000 tags with 1 MiB text and verified saving.

## Passed checks and corrected contrast interpretation

The sampled enabled secondary text passed the 4.5:1 reference using its WPF brush
composited onto the captured displayed background: **6.065:1 light**, **9.492:1 dark**.
This measures the sampled hosted view, not every application text/color pair.
Earlier inferred-background/window-DC readings are excluded: they omitted DWM
composition of translucent Fluent brushes. The final capture uses the composited
desktop, verified foreground/client bounds and DWM synchronization.

The earlier observation of approximately 3.15:1 between antialiased glyph pixels
and the light screenshot background does **not** establish a WCAG contrast failure.
[W3C's contrast guidance](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html)
uses underlying foreground/background colors rather than antialiased text pixels.
Thin text can still look faint and deserves visual readability review.

Actual Windows high contrast was enabled and observed by WPF in both processes;
the title input retained keyboard focus and the review action stayed visible.
Screenshot inspection shows a visible cyan title focus outline. This is a sampled
focus/content check; complete high-contrast navigation and all focus indicators
remain pending. Other superseded runs contain harness initialization, shutdown or
foreground/capture limitations and are not substitutes for the final results.

## Method and remaining limits

Explicitly applied the repository's [web-design-guidelines skill](../.agents/skills/web-design-guidelines/SKILL.md).
Fetched its [current rules](https://raw.githubusercontent.com/vercel-labs/web-interface-guidelines/main/command.md)
with HTTP 200 on 2026-10-10 (SHA-256 `d246b026f4f29b5823a9cc857f9edf3d2507002e055e32040cadeaf3b38e0234`).
Applied relevant draft/state, focus, naming, live-notification, layout and large-list
criteria to native WPF. HTML/ARIA/CSS, browser navigation and web-asset rules do
not directly apply; this review does not certify WPF accessibility compliance.

Host: Windows 11 Enterprise build 26200, ARM64, x64 application under emulation,
interactive elevated hosted VM with SDKs installed, 96 DPI/100%, 1024×768.
Narrator speech and physical Windows 150%/200% scale changes were skipped in each
process. LayoutTransform 1×/1.5×/2× tests are layout simulations, not OS DPI changes.
Clean physical x64, standard-user/offline installation, full manual keyboard/UIA
navigation and broader contrast/readability acceptance remain pending in
[Windows acceptance](WINDOWS_ACCEPTANCE.md).

Local cross-build of the final harness passed with zero warnings/errors;
`actionlint`, PowerShell syntax validation and `git diff --check` passed.
The original baseline also contained LNK4044 (ignored `/link`) and pinned-action
Node 20 deprecation notices. PR #22 removes qpdf's MSVC driver separator from
linker options while retaining `wsetargv.obj`, and pins official Node 24 actions.
Passing baseline tests alone does not clear additional findings.


## Implemented corrections (PR #22)

- `src/PdfMetaStudio.App/ViewModels/TagTreeViewModel.cs:62` — **P1/P3:** tag values/types and raw XML are retained in memory by stable tag/stream key; search, selection and preview keep drafts. Filtered expansion no longer replaces unfiltered expansion. Apply/reset remove only the relevant draft; explicit deletion cancels input for removed nodes.
- `src/PdfMetaStudio.App/ViewModels/EditorViewModel.cs:136` — **P1:** close and reload guards include tag/XML input. Review points to unapplied input and requires apply/reset; a previously reviewed save/replace cannot silently omit new drafts. Drafts are not persisted or silently added to the PDF edit session.
- `src/PdfMetaStudio.App/ViewModels/DateEditorViewModel.cs:105` — **P2:** failed component application identifies the invalid year/month/day/time/zone/precision input; `DateEditorView.xaml.cs:20` focuses it. Date parsing/serialization rules remain unchanged.
- `src/PdfMetaStudio.App/Views/EditorView.xaml:193` — **P2:** read-only dimensions, Base64 packet and private-byte diagnostics receive stable UIA names.
- `src/PdfMetaStudio.App/Views/EditorView.xaml:169` — **P2:** an outer constrained-size scroll owner and a scrollable XML panel keep apply/reset and footer actions reachable. Expanded XML reserves enough logical height; smaller/scaled viewports scroll rather than clip the action region.
- `src/PdfMetaStudio.App/Views/EditorView.xaml:533` — **P2:** both scope radios and the document-scope selector reflect the same model in both directions; obsolete checked handlers are removed.
- `src/PdfMetaStudio.App/Views/EditorView.xaml:475` — **P2:** a bounded recycling panel virtualizes review rows. The interactive header/footer remain realized for keyboard navigation; the same collection can scroll to the final row and footer.
- `src/PdfMetaStudio.App/Views/LiveTextBlock.cs:11` — **P2:** dynamic status/error regions raise UIA live-region notifications when text changes and clients listen. Static `LiveSetting` alone was insufficient.
- `src/PdfMetaStudio.App/Views/EditorView.xaml.cs:15` — **P2, additional observation:** the first production dark GUI run displayed date inputs but its external UIA tree exposed only empty data-item peers after section/size switches. Refresh existing peers after field templates become visible; a new external-client regression covers these transitions.
- `worker/CMakeLists.txt:39` / `.github/workflows/windows.yml:31` — **P3:** omit qpdf's unnecessary MSVC driver separator, retain the required wide-argument object, and update SHA-pinned official CI actions to their Node 24 releases. Shipped dependency versions and installation/versioning behavior are unchanged.

Original report and line references remain above for traceability. The first correction run
[38053960218](https://github.com/qenuternis2/pdf-meta-studio/actions/runs/38053960218)
confirmed the draft/state/focus/UIA notification fixes and 7–8 realized review containers,
but still failed all six XML size/theme checks and one production calendar/UIA case.
Those failures were retained and prompted the subsequent layout/peer corrections;
this intermediate run is not final acceptance.


## Verification of corrections

[Final correction results](validation/ui-audit-fixed-windows11.json) preserve source,
provenance, host, all checks and remaining limits separately from the original failures.
[UI run 38057802192](https://github.com/qenuternis2/pdf-meta-studio/actions/runs/38057802192)
from `c44dd01644b310cce1e95d811fb4d598cb4c3ce7` completed successfully:
**34 PASS, 4 SKIP, zero FAIL/ERROR/BLOCKED**, plus all three production GUI cases
(light, dark and 10,000 tags/1 MiB text) with verified saves. It used
`additional_only=false`; the rebuilt application and harness match that source.
Native inputs matched successful full Windows package run `38054424785`.

[Full Windows run 38057801600](https://github.com/qenuternis2/pdf-meta-studio/actions/runs/38057801600)
also passed for the same `c44dd01644b310cce1e95d811fb4d598cb4c3ce7` source.
It rebuilt the native worker, managed application and MSI, and passed Windows
Server 2022 and Windows 11 ARM64 acceptance. Windows 11 recorded **95 Core tests
with no skips**, **50 native checks with three explicit Linux-only skips**, and
all three production GUI scenarios. Its corpus checked 628 files, opened 586
and verified 470 saves with zero unexpected failures; seven preservation-blocked
files were refused rather than counted as saves. MSI installation/uninstallation
and upgrade from 0.3.0 to 0.3.1 passed, verifying 424 payload files and preserving
both control documents. The completed build log contains neither LNK4044 nor
Node 20 deprecation notices. This is a hosted elevated ARM64/emulated-x64 result,
not clean physical x64 or standard-user/offline acceptance. The workflow's
`windows11-acceptance` and `PdfMetaStudio-installer-win-x64` artifacts retain the
detailed reports and built MSI; no new release or version change is part of this PR.

All ten original confirmed findings are addressed. The additional date-peer transition,
close/save/reload guards and review keyboard regressions also passed in both themes.
Review retained 7 initial / 8 post-scroll containers for 1,000 rows, reached the last
row and footer, and moved keyboard focus to the footer via Tab traversal. Recorded
765/785 ms includes keyboard traversal and scrolling; it is not the same timing
scope as the original layout-only observation. Sampled contrast remains 6.065:1
light and 9.492:1 dark. Actual high-contrast title focus/content passed again.
Inspected XML action screenshots at constrained scale confirm the complete button.

The guarded intermediate diagnostic `38057386351` still found 5 logical pixels of
outer clipping in dark 1.5x even after a bounded two-second layout wait. The final
view brings the focused XML action and its outline into view after layout updates.
Viewport assertions retain all ancestor clips; the timeout does not waive visibility.
The hosted-image desktop guard is shared by baseline/additional checks, restricted
to disposable Windows ARM GitHub Actions, and stopped in `finally`. A separate
foreground-blocked run is excluded rather than counted as a passed application check.

Local baseline Core tests passed 85/85 before changes; the corrected suite passed
95/95 with no skips. WPF app/harness cross-build passed without warnings/errors;
PowerShell syntax, `actionlint` and whitespace checks passed. Local native tests
passed 45 with eight explicit environment/fixture skips, not eight successful tests.
Narrator speech, physical DPI and clean x64/non-elevated/offline acceptance remain
pending; the hosted results do not certify complete accessibility or security.
