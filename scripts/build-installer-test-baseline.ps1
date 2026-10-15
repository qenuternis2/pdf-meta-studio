# A test-only EXE baseline made from the verified published 0.3.3 application.
# It is not published as a second 0.3.3 release.
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or $env:GITHUB_ACTIONS -ne 'true') {
    throw 'Installer baseline preparation requires disposable Windows GitHub Actions'
}
$root = Split-Path -Parent $PSScriptRoot
$directory = Join-Path $root 'build\installer-baseline'
if (Test-Path -LiteralPath $directory) { throw 'Baseline preparation requires a fresh output directory' }
$archive = Join-Path $env:RUNNER_TEMP 'PdfMetaStudio-v0.3.3-win-x64.zip'
$url = 'https://github.com/qenuternis2/pdf-meta-studio/releases/download/v0.3.3/PdfMetaStudio-v0.3.3-win-x64.zip'
$hash = 'fccbf1c631c043e40d65f72e9c06414701a8a776b5c369ac67eb9a615a725c1b'
Invoke-WebRequest -Uri $url -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $hash) { throw 'Baseline ZIP SHA-256 mismatch' }
Expand-Archive -LiteralPath $archive -DestinationPath $directory
& (Join-Path $PSScriptRoot 'build-installer.ps1') -PublishDir (Join-Path $directory 'PdfMetaStudio') -Version '0.3.3' -OutputDir $directory
@{ version = '0.3.3'; sourceUrl = $url; sha256 = $hash; scope = 'Test-only EXE baseline; never a public release' } |
    ConvertTo-Json | Set-Content (Join-Path $directory 'provenance.json') -Encoding utf8
