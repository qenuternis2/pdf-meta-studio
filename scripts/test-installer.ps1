$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$installer = (Get-ChildItem "$root\dist\installer\*.msi" | Select-Object -First 1).FullName
if (-not $installer) { throw 'Installer missing' }
$installed = Join-Path $env:LOCALAPPDATA 'PdfMetaStudio'
if (Test-Path $installed) { throw 'Installer acceptance requires a clean installation directory' }
$process = Start-Process msiexec.exe -ArgumentList @('/i', "`"$installer`"", '/qn', '/norestart', '/l*v', "`"$root\installer-install.log`"") -Wait -PassThru
if ($process.ExitCode -notin @(0, 3010)) { throw "Installer failed: $($process.ExitCode)" }
foreach ($file in @('PdfMetaStudio.exe','pdfmeta-worker.exe','licenses\MANIFEST.txt')) {
    if (-not (Test-Path (Join-Path $installed $file))) { throw "Installed file missing: $file" }
}
& powershell.exe -NoProfile -File "$root\scripts\gui-smoke.ps1" -Exe "$installed\PdfMetaStudio.exe" -Pdf "$root\tests\fixtures\rich.pdf" -OutDir "$root\installer-smoke"
if ($LASTEXITCODE) { throw 'Installed GUI smoke failed' }
$process = Start-Process msiexec.exe -ArgumentList @('/x', "`"$installer`"", '/qn', '/norestart', '/l*v', "`"$root\installer-uninstall.log`"") -Wait -PassThru
if ($process.ExitCode -notin @(0, 3010)) { throw "Uninstall failed: $($process.ExitCode)" }
if (Test-Path $installed) { throw 'Install directory was not removed after uninstall' }
Write-Host 'PASS per-user install, installed GUI workflow and uninstall'
