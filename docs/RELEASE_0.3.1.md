# Version 0.3.1

This servicing build includes the corrections documented in [the security audit](SECURITY_AUDIT.md): retained temporary-file streams, protected Windows pending output and snapshots, backup cleanup ownership, Expat 2.9.0 and the self-contained .NET 10.0.12 runtime. CI Actions are pinned to upstream commits and checkout credentials are not persisted for subsequent build steps.

The application/MSI version is 0.3.1. The existing per-user installation scope and MSI UpgradeCode are preserved. No product functionality or protocol change is introduced by this version bump.

## Upgrade acceptance

Both Windows Server and Windows 11 ARM CI run the existing clean install/GUI/uninstall check, followed by a new published-0.3.0 upgrade check. The legacy MSI is downloaded from the official repository release and must match its pinned SHA-256 before installation. This check runs only in disposable Windows GitHub Actions; the old application does not open PDF documents.

The upgrade check verifies:

- The legacy registration is removed and the new product is registered as 0.3.1.
- Every installed payload file matches the newly published file by SHA-256, including the worker, runtime, licenses and validation reports.
- The upgraded application's GUI can open, edit, save and independently verify a synthetic PDF.
- Two synthetic documents, inside and outside the installation directory, survive upgrade and uninstall unchanged.
- Uninstall removes the registered payload and the new product registration.

CI artifacts retain the MSI logs, GUI evidence and machine-readable upgrade report. The hosted Windows 11 x64 application runs under ARM64 emulation; these results do not establish a standard-user physical x64/offline upgrade.

## Remaining boundaries

Packages remain unsigned and the worker retains the user's privileges. EFS/reparse/SMB and real second-account filesystem checks, crash-retained snapshots, Narrator and physical DPI acceptance remain separate tasks. SHA-256 checks detect corruption or substitution relative to the pinned value; they do not replace an independent publisher signature.
