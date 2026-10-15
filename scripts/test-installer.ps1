$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'installer-test-common.ps1')
Assert-FreshInstall
$output = Join-Path $TestRoot 'installer-smoke'
New-Item -ItemType Directory -Path $output | Out-Null
& powershell.exe -NoProfile -File (Join-Path $PSScriptRoot 'test-setup-wizard.ps1') -SetupExe $CurrentSetup -OutDir $output
if ($LASTEXITCODE) { throw 'Interactive EXE wizard acceptance failed' }
Assert-FreshInstall
$installExit = Invoke-Setup $CurrentSetup (Join-Path $TestRoot 'installer-install.log')
if ($installExit -ne 0) { throw "EXE installation failed: $installExit" }
$count = Assert-InstalledPayload
Assert-Registration
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'PDF Meta Studio.lnk'
if (Test-Path -LiteralPath $desktop) { throw 'Default installation unexpectedly created a desktop shortcut' }
Invoke-InstalledGui (Join-Path $TestRoot 'tests\fixtures\rich.pdf') $output
$uninstallExit = Invoke-SetupUninstall (Join-Path $TestRoot 'installer-uninstall.log')
if (Test-Path -LiteralPath $InstallDir) { throw 'Empty installation directory was retained' }
@{ status = 'PASS'; version = $SetupVersion; format = 'standalone EXE'; verifiedPayloadFiles = $count
    installExitCode = $installExit; uninstallExitCode = $uninstallExit; scope = 'Per-user installation in disposable hosted Windows'
} | ConvertTo-Json | Set-Content (Join-Path $output 'results.json') -Encoding utf8
Write-Host 'PASS EXE wizard, per-user install, exact payload, registration, shortcut, installed GUI and uninstall'
