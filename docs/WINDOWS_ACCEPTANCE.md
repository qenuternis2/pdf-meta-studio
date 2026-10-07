# Windows 11 acceptance record

Target: clean Windows 11 x64, standard user, no .NET/Visual Studio installed. Record OS build, application version, source commit, screen resolution, physical scale and the resulting screenshots/logs. **Manual checks below are pending, not passed.**

| Check | Procedure and expected result | Status |
|---|---|---|
| Install and uninstall | Install the per-user MSI without elevation; run offline; check Start menu and license directory; uninstall removes program files and shortcut but keeps user documents. | Pending Windows 11; separate Server CI coverage |
| Start screen | Exactly one actionable button, `Change meta info`; cancelling the system dialog keeps the start screen. | Automated smoke; manual pending |
| Narrator | Navigate every section, tree, component/language/item control, delete/restore state and review. Labels, values, errors and progress are announced in Russian, with no unexplained unlabeled focus targets. | Pending |
| Keyboard | Tab and Shift+Tab reach all controls without trapping focus; Ctrl+Z/Y, Ctrl+Shift+Z, Ctrl+S and Ctrl+W work; opening the save menu and selecting its actions works from the keyboard. | Pending |
| Physical scale/theme | Restart at 100%, 150% and 200%, light and dark themes; resize; inspect calendar, item controls, tree, XML, dialogs and review. No clipped actions or inaccessible scroll regions. | Pending |
| Standard workflows | Move authors with qualifiers, add/delete one language, preserve fractional and partial dates, rename a structure, insert a compound array item and undo/redo each operation. | Native/ViewModel tests; manual pending |
| Protection | Signed/restricted PDFs initially permit browsing/export; owner password enables authorized editing; signed editing requires explicit copy consent; encryption and restrictions stay intact. | Native tests; manual pending |
| External file change | Replace or modify the source outside the editor; original replacement is blocked; reload opens current bytes; save-copy preserves the opened session instead. | Integration tests; manual pending |
| Long paths and data | Open and save a Unicode/UNC path and a local path over 260 characters on a long-path-enabled system; inspect 1 MiB multiline text and 10,000 tags; cancel processing while keyboard navigation stays responsive. | Pending |
| Recovery | Terminate the worker during preview/write; show the error, keep edits, restart and save elsewhere. Test access denial, locked original, disk full and failed backup; original and preexisting targets remain intact. | Native/integration tests; manual pending |
| Preservation | Inspect representative links, bookmarks, form values/appearances and attachments with an independent viewer; compare independent corpus report exclusions and refused files. | Corpus checks; manual pending |
| Profiles | Show declared PDF/A/X/UA separately from validation; with local veraPDF, report its actual validated profile; without a matching validator keep other profiles unverified. | Parser tests; external validator/manual pending |

Do not fill a pending result from expectation. Attach actual evidence and failures, with reproducible fixture and steps, before calling Windows 11 acceptance complete.
