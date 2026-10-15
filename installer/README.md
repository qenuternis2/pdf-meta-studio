# Standalone Windows installer

The primary installation asset is `PdfMetaStudio-<version>-win-x64-setup.exe`.
It is a complete offline Inno Setup installer, not an MSI bootstrapper.
`scripts/build-installer.ps1` reads the application version from
`Directory.Build.props` and packages the published self-contained application.
The Russian/English wizard exposes destination, optional desktop shortcut,
confirmation and optional launch. Silent installation never launches the app.

## Tool provenance

`inno-setup.lock.json` pins Inno Setup 7.1.0 to its official
[jrsoftware/issrc release](https://github.com/jrsoftware/issrc/releases/tag/is-7_1_0)
and official GitHub asset SHA-256. The build verifies the downloaded compiler
installer before executing its `/PORTABLE=1 /CURRENTUSER` extraction, verifies
the compiler version, and copies its original license to
`dist/installer/InnoSetup-LICENSE.txt`. Keep that file with published setup assets.
Tool acquisition is build-only; the product setup downloads nothing.

## Ownership and compatibility

- `PrivilegesRequired=lowest` installs for the current user under
  `%LOCALAPPDATA%\Programs\PdfMetaStudio`; no elevation, service or autostart.
- Registration is HKCU and the flat Start menu shortcut uses the application icon.
- The legacy MSI owns `%LOCALAPPDATA%\PdfMetaStudio` and a Start menu subfolder.
  The EXE refuses that directory and its descendants. The packages can coexist;
  removing the old MSI independently must leave the new installation intact.
- Upgrades and same-version reinstalls share one stable Inno AppId. Downgrades
  and unrecognized installed version strings are refused. Running applications
  are not forcibly closed.
- Uninstallation removes installer-owned payload only, with no wildcard
  `[UninstallDelete]` rule. Unowned PDF documents are retained.
- `generate.py` remains a legacy MSI source generator for maintenance and external
  callers. Normal builds do not install WiX or create an MSI.

## Validation and publication

Installer execution tests run only in disposable Windows GitHub Actions VMs.
They check a visible wizard through Ready to Install and cancellation, full
payload hashes, HKCU registration, shortcut target/icon, installed application
GUI, upgrade/reinstall, downgrade refusal, retained PDFs and uninstallation.
The compatibility test installs the checksum-verified published 0.3.3 MSI,
rejects an EXE install into its directory, and removes the MSI while verifying
the new EXE installation.

For EXE-to-EXE upgrade coverage, CI creates a **test-only** 0.3.3 EXE using the
current installer template and checksum-verified published 0.3.3 application
bits. This is not a previously shipped EXE and is never published as a release.
CI records baseline provenance and distinct results for this scenario and the
real legacy MSI scenario. Physical Windows x64, offline and standard-user
acceptance remain separate manual checks; hosted runners are elevated.

A version tag runs the full Windows workflow and publishes validated assets.
Alternatively, run `publish-release.yml` after merging a successful full
`windows.yml` run without squashing its tested commit. Supply that run ID and
a new version tag. Publication requires successful build and Windows 11 jobs,
the tested commit to be an ancestor of main, exactly identical tracked source
trees, a matching application version and a new tag. It downloads the exact
tested artifacts, checks their source manifest, and publishes versioned ZIP/EXE
assets, the Inno license and SHA-256 manifests without rebuilding them.
