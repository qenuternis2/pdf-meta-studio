# Windows UI audit — 2026-10-10

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

## Executed checks and evidence

[Machine-readable results](validation/ui-audit-windows11.json) preserve source commits,
host information, native provenance, every additional check and baseline results.
Application source, native source, installer and dependency inputs are unchanged
between the baseline and final UI audit; only test automation/documentation changed.
The findings above remain **unfixed application behavior**. Functional fixes are
outside this testing task and require the separately requested agreement.

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
Existing native build warning LNK4044 (ignored `/link`) and GitHub's pinned-action
Node 20 deprecation notices remain CI maintenance items; action versions were not
changed in this task. Passing baseline tests does not clear the additional findings.
