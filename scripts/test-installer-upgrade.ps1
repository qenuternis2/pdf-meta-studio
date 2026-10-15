$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'installer-test-common.ps1')
Assert-FreshInstall
$output = Join-Path $TestRoot 'installer-upgrade'
if (Test-Path -LiteralPath $output) { throw 'Upgrade acceptance requires a fresh evidence directory' }
New-Item -ItemType Directory -Path $output | Out-Null
$baseline = Join-Path $TestRoot 'build\installer-baseline\PdfMetaStudio-0.3.3-win-x64-setup.exe'
$baselinePayload = Join-Path $TestRoot 'build\installer-baseline\PdfMetaStudio'
$oldExit = Invoke-Setup $baseline (Join-Path $output 'baseline-install.log')
if ($oldExit -ne 0) { throw "Baseline EXE installation failed: $oldExit" }
$null = Assert-InstalledPayload $baselinePayload
Assert-Registration '0.3.3'
$inside = Join-Path $InstallDir 'user-sentinel.pdf'
$outside = Join-Path $output 'user-document.pdf'
Copy-Item -LiteralPath (Join-Path $TestRoot 'tests\fixtures\rich.pdf') -Destination $inside
Copy-Item -LiteralPath $inside -Destination $outside
$documentHash = (Get-FileHash -LiteralPath $inside -Algorithm SHA256).Hash
$upgradeExit = Invoke-Setup $CurrentSetup (Join-Path $output 'upgrade-install.log')
if ($upgradeExit -ne 0) { throw "EXE upgrade failed: $upgradeExit" }
$count = Assert-InstalledPayload
Assert-Registration
$reinstallExit = Invoke-Setup $CurrentSetup (Join-Path $output 'reinstall.log')
if ($reinstallExit -ne 0) { throw "Same-version EXE reinstall failed: $reinstallExit" }
$null = Assert-InstalledPayload
$downgradeExit = Invoke-Setup $baseline (Join-Path $output 'rejected-downgrade.log')
if ($downgradeExit -eq 0) { throw 'EXE downgrade was not rejected' }
$null = Assert-InstalledPayload
Assert-Registration
Invoke-InstalledGui $outside (Join-Path $output 'gui')
$uninstallExit = Invoke-SetupUninstall (Join-Path $output 'upgrade-uninstall.log')
foreach ($document in @($inside, $outside)) {
    if ((Get-FileHash -LiteralPath $document -Algorithm SHA256).Hash -ne $documentHash) {
        throw 'EXE upgrade/reinstall/downgrade refusal/uninstall changed an unowned PDF'
    }
}
Remove-Item -LiteralPath $inside -Force
if (@(Get-ChildItem -LiteralPath $InstallDir -Force).Count -ne 0) { throw 'Unexpected remaining installation files' }
Remove-Item -LiteralPath $InstallDir -Force
@{ status = 'PASS'; baselineVersion = '0.3.3'; version = $SetupVersion; format = 'standalone EXE'
    baselineScope = 'Test-only EXE package of verified published 0.3.3 application bits'
    verifiedPayloadFiles = $count; preservedDocuments = 2; documentSha256 = $documentHash
    baselineExitCode = $oldExit; upgradeExitCode = $upgradeExit; reinstallExitCode = $reinstallExit
    rejectedDowngradeExitCode = $downgradeExit; uninstallExitCode = $uninstallExit
    limits = 'Disposable hosted Windows; clean physical x64 standard-user/offline acceptance remains untested.'
} | ConvertTo-Json | Set-Content (Join-Path $output 'results.json') -Encoding utf8
Write-Host "PASS EXE 0.3.3 -> $SetupVersion upgrade, reinstall, downgrade refusal, payload, GUI and document retention"
