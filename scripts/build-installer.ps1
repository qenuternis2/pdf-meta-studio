param(
    [string]$PublishDir = "$PSScriptRoot\..\dist\PdfMetaStudio",
    [string]$Version = '',
    [string]$OutputDir = "$PSScriptRoot\..\dist\installer"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Version) {
    $Version = ([xml](Get-Content "$root\Directory.Build.props" -Raw)).Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Installer version must have three numeric components' }
$PublishDir = [IO.Path]::GetFullPath($PublishDir)
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
foreach ($required in @('PdfMetaStudio.exe', 'pdfmeta-worker.exe', 'licenses\MANIFEST.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PublishDir $required) -PathType Leaf)) {
        throw "Published installer input missing: $required"
    }
}
$pin = Get-Content "$root\installer\inno-setup.lock.json" -Raw | ConvertFrom-Json
$toolBase = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$tools = Join-Path $toolBase "pdfmeta-inno-$($pin.version)"
$compiler = Join-Path $tools 'ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    $download = Join-Path $toolBase "pdfmeta-innosetup-$($pin.version)-x64.exe"
    Invoke-WebRequest -Uri $pin.url -OutFile $download
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $pin.sha256) {
        throw 'Inno Setup compiler archive SHA-256 mismatch; refusing to execute'
    }
    # Inno Setup's official /PORTABLE mode avoids SDK registration and shortcuts.
    $process = Start-Process -FilePath $download -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER',
        '/PORTABLE=1', ('/DIR="' + $tools + '"')
    ) -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Inno Setup compiler extraction failed: $($process.ExitCode)" }
}
$actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($compiler)
if ("$($actualVersion.FileMajorPart).$($actualVersion.FileMinorPart).$($actualVersion.FileBuildPart)" -ne $pin.version) {
    throw 'Unexpected Inno Setup compiler version'
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $tools 'license.txt') -Destination (Join-Path $OutputDir 'InnoSetup-LICENSE.txt') -Force
& $compiler "/DAppVersion=$Version" "/DPublishDir=$PublishDir" "/DOutputDir=$OutputDir" "$root\installer\PdfMetaStudio.iss"
if ($LASTEXITCODE) { throw 'EXE installer compilation failed' }
$output = Join-Path $OutputDir "PdfMetaStudio-$Version-win-x64-setup.exe"
if (-not (Test-Path -LiteralPath $output -PathType Leaf)) { throw 'Compiled EXE installer missing' }
Write-Host "Built EXE installer: $output"
