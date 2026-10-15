# Compatibility test only. The shipped EXE never starts msiexec.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'installer-test-common.ps1')
Assert-FreshInstall
if (Test-Path -LiteralPath $LegacyInstallDir) { throw 'Legacy MSI acceptance requires a clean legacy directory' }
$output = Join-Path $TestRoot 'installer-legacy'
New-Item -ItemType Directory -Path $output | Out-Null
$package = Join-Path $env:RUNNER_TEMP 'PdfMetaStudio-0.3.3-win-x64.msi'
$url = 'https://github.com/qenuternis2/pdf-meta-studio/releases/download/v0.3.3/PdfMetaStudio-0.3.3-win-x64.msi'
$hash = '45f100bafaa4b3256ca86886ff7e95b673434c3a8b3fcbd29afc143607d2783a'
Invoke-WebRequest -Uri $url -OutFile $package
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $hash) { throw 'Legacy MSI SHA-256 mismatch' }
$msiexec = Join-Path $env:SystemRoot 'System32\msiexec.exe'
function Invoke-LegacyMsi([string]$Operation, [string]$Name) {
    $process = Start-Process -FilePath $msiexec -ArgumentList @(
        $Operation, ('"' + $package + '"'), '/qn', '/norestart', '/l*v', ('"' + (Join-Path $output $Name) + '"')
    ) -Wait -PassThru
    if ($process.ExitCode -notin @(0, 3010)) { throw "Legacy MSI test failed: $($process.ExitCode)" }
}
Invoke-LegacyMsi '/i' 'legacy-install.log'
$legacyHashes = @{}
foreach ($file in Get-ChildItem -LiteralPath $LegacyInstallDir -Recurse -File) {
    $legacyHashes[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
}
if ($legacyHashes.Count -eq 0) { throw 'Legacy MSI payload missing' }
$sentinel = Join-Path $LegacyInstallDir 'user-sentinel.pdf'
Copy-Item -LiteralPath (Join-Path $TestRoot 'tests\fixtures\rich.pdf') -Destination $sentinel
$sentinelHash = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
$rejected = Invoke-Setup $CurrentSetup (Join-Path $output 'rejected-legacy-directory.log') @('/DIR="' + $LegacyInstallDir + '"')
if ($rejected -eq 0) { throw 'EXE setup accepted the legacy MSI directory' }
foreach ($path in $legacyHashes.Keys) {
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $legacyHashes[$path]) { throw 'Refused install changed legacy MSI payload' }
}
$installExit = Invoke-Setup $CurrentSetup (Join-Path $output 'exe-install.log')
if ($installExit -ne 0) { throw "Independent EXE setup failed: $installExit" }
$count = Assert-InstalledPayload
Assert-Registration
Invoke-LegacyMsi '/x' 'legacy-uninstall.log'
$null = Assert-InstalledPayload
Assert-Registration
$uninstallExit = Invoke-SetupUninstall (Join-Path $output 'exe-uninstall.log')
if ((Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -ne $sentinelHash) { throw 'Uninstall changed a legacy user document' }
Remove-Item -LiteralPath $sentinel -Force
if (@(Get-ChildItem -LiteralPath $LegacyInstallDir -Force).Count -ne 0) { throw 'Unexpected legacy leftovers' }
Remove-Item -LiteralPath $LegacyInstallDir -Force
@{ status = 'PASS'; legacyVersion = '0.3.3'; version = $SetupVersion; rejectedLegacyDirectoryExitCode = $rejected
    verifiedPayloadFiles = $count; preservedLegacyDocuments = 1; legacySource = $url; legacySha256 = $hash
    installExitCode = $installExit; uninstallExitCode = $uninstallExit
    scope = 'Old MSI and new EXE use separate directories and shortcuts; removing old MSI preserves new payload'
} | ConvertTo-Json | Set-Content (Join-Path $output 'results.json') -Encoding utf8
Write-Host 'PASS independent EXE/MSI coexistence, legacy-directory refusal and legacy uninstall isolation'
