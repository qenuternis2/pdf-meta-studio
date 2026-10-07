# Windows 11 hosted acceptance

The `windows11` job in `.github/workflows/windows.yml` uses `runs-on: windows-11-arm`. It consumes the application, MSI and native test tools from the same workflow's x64 build. Release publication waits for this job.

The application and worker remain x64; Windows 11 ARM64 runs them through its built-in emulation. This job establishes compatibility on that host, not a native ARM64 product or clean physical x64-machine acceptance.

`windows11-acceptance` contains `host.json`, desktop-preparation details, native test/corpus JSON, managed test TRX, installation/uninstallation logs, pinned Poppler identity and GUI screenshots/results. The host record checks CIM Windows 11 client/build and ARM64 CPU architecture, and records the application PE machine/hash, runner architecture, elevation, display size and observed DPI. Windows 11 may still report `Windows 10` in the legacy registry product name; that value is preserved for diagnostics and does not determine the host check.

The automated cases cover:

- Native worker tests and the pinned qpdf corpus, including supported NTFS sharing/ACL and long-path cases; every refusal and skip remains explicit.
- Independent pypdf inspection and byte-identical rendered pages using Poppler 26.09.0 (download SHA-256 verified).
- Managed/core/ViewModel/IPC tests against the actual worker and native IPC test peer.
- Per-user MSI installation, installed application edit/save/reopen and uninstall.
- Tab/Shift+Tab, focused Ctrl+Z/Y/Shift+Z/S/W and keyboard save-menu/copy actions.
- Observed light/dark backgrounds, four sections at 640×480 and 1000×700.
- Calendar popup visibility, Escape dismissal and preservation of the original date.
- 10,000-tag search, complete 1 MiB text inspection/navigation and exact saved tag/value preservation.

The image can display first-sign-in privacy setup over the entire desktop. This is tracked upstream in [runner-images issue #14069](https://github.com/actions/runner-images/issues/14069). `prepare-windows-desktop.ps1` is restricted to disposable GitHub Actions VMs, disables that setup experience by policy and stops WWAHost/setup brokers and first-login performance/Start-menu/WSL-update surfaces immediately before each keyboard automation case; it does not accept privacy defaults. This preparation is recorded. ARM CI fills and submits Shell file dialogs through verified native edit/button messages because their UIA peers may be missing and unrelated startup windows can intercept global path keystrokes; application keyboard checks still send real keys. Each ARM CI GUI case also watches for relaunched setup/WSL/Terminal processes until completion and records every termination in `desktop-guard.log`; the guard is stopped in `finally`.

The optional `gui_only` workflow dispatch also uses Windows 11 ARM, rebuilding managed code over a specified earlier native package. Its `enginePackageRun` identifies that reuse. It is a GUI diagnostic run and does not establish native/MSI acceptance for current source.

Results must be read from completed jobs and their artifacts. A runner label, successful compilation or screenshot does not establish every acceptance requirement. Hosted VMs contain SDKs/tools and run with the recorded privilege level; clean-machine, non-elevated/offline installation, system scale changes to 150%/200%, UNC workflows, complete Narrator speech and independent viewer inspection remain separate pending checks in [Windows acceptance](WINDOWS_ACCEPTANCE.md).
