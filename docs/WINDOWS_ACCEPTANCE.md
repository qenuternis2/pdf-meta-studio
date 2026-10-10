# Windows 11 acceptance record

Target: clean Windows 11 x64, standard user, no .NET/Visual Studio installed. Record OS build, application version, source commit, screen resolution, physical scale and the resulting screenshots/logs. **Manual checks below are pending, not passed.**

| Check | Procedure and expected result | Status |
|---|---|---|
| Install and uninstall | Install the per-user MSI without elevation; run offline; check Start menu and license directory; uninstall removes program files and shortcut but keeps user documents. | Windows 11 ARM CI covers install/installed GUI/uninstall; clean x64, non-elevated/offline and document/shortcut retention acceptance remains pending |
| Start screen | Exactly one actionable button, `Change meta info`; cancelling the system dialog keeps the start screen. | Automated smoke; manual pending |
| Narrator | Navigate every section, tree, component/language/item control, delete/restore state and review. Labels, values, errors and progress are announced in Russian, with no unexplained unlabeled focus targets. | Pending |
| High contrast | Enable actual Windows high contrast; navigate every section and inspect focused inputs/actions, labels and state indicators. | Additional Windows 11 ARM audit toggles actual system state and verifies sampled title focus/content; screenshots inspected for title focus outline; full navigation remains pending |
| Keyboard | Tab and Shift+Tab reach all controls without trapping focus; Ctrl+Z/Y, Ctrl+Shift+Z, Ctrl+S and Ctrl+W work; opening the save menu and selecting its actions works from the keyboard. | Windows 11 ARM and Server automation cover forward/reverse focus cycles, focused shortcuts, keyboard save-menu opening and save-copy; complete Windows 11 navigation remains pending |
| Physical scale/theme | Restart at 100%, 150% and 200%, light and dark themes; resize; inspect calendar, item controls, tree, XML, dialogs and review. No clipped actions or inaccessible scroll regions. | Windows 11 ARM and Server automation verify observed light/dark backgrounds, four sections at 640×480 and 1000×700, and visible review actions; Windows 11 ARM additionally checks calendar popup visibility/Escape/date preservation; system scale changes to 150%/200% remain pending |
| Standard workflows | Move authors with qualifiers, add/delete one language, preserve fractional and partial dates, rename a structure, insert a compound array item and undo/redo each operation. | Native/ViewModel tests; manual pending |
| Protection | Signed/restricted PDFs initially permit browsing/export; owner password enables authorized editing; signed editing requires explicit copy consent; encryption and restrictions stay intact. | Native tests; manual pending |
| External file change | Replace or modify the source outside the editor; original replacement is blocked; reload opens current bytes; save-copy preserves the opened session instead. | Integration tests; manual pending |
| Long paths and data | Open and save a Unicode/UNC path and a local path over 260 characters on a long-path-enabled system; inspect 1 MiB multiline text and 10,000 tags; cancel processing while keyboard navigation stays responsive. | Windows 11 ARM native long Unicode paths (when enabled) plus automated 10,000-tag search, complete 1 MiB value inspection/navigation and verified save; UNC and interactive cancellation on Windows 11 remain pending |
| Recovery | Terminate the worker during preview/write; show the error, keep edits, restart and save elsewhere. Test access denial, locked original, disk full and failed backup; original and preexisting targets remain intact. | Native/integration tests; manual pending |
| Preservation | Inspect representative links, bookmarks, form values/appearances and attachments with an independent viewer; compare independent corpus report exclusions and refused files. | Corpus checks; manual pending |
| Profiles | Show declared PDF/A/X/UA separately from validation; with local veraPDF, report its actual validated profile; without a matching validator keep other profiles unverified. | Parser tests; external validator/manual pending |

Do not fill a pending result from expectation. Attach actual evidence and failures, with reproducible fixture and steps, before calling Windows 11 acceptance complete.

The `windows-acceptance` CI artifact contains per-case logs, screenshots and `results.json` for the exact source commit and OS. `scripts/windows-acceptance.ps1` temporarily changes only the test user's application theme and restores it in `finally`. Screenshots/resize tests do not change physical DPI and do not verify Narrator speech. Run the script on Windows 11 to collect that machine's automated evidence too.

[Windows 11 CI](WINDOWS11_CI.md) records the ARM64 host and x64 emulation boundary. Read completed job artifacts before asserting a passed automated result; SDK-equipped/elevated hosted VMs do not fulfill clean x64 standard-user acceptance.

[Additional UI audit (2026-10-10)](UI_AUDIT.md) records 24 failed check instances
across two themes, including unapplied draft loss, clipped expanded XML actions
at 640×480, inconsistent scope choices, invalid-date focus and UIA naming/events.
It also records passed sampled contrast/high-contrast checks and the exact source,
host and baseline evidence. Existing production GUI acceptance passed; the added
findings remain open and Windows acceptance is incomplete.
