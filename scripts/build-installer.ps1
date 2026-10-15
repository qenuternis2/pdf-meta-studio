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
# The console launcher has independent PE metadata; query the actual engine.
$actualVersion = (& $compiler --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $actualVersion -ne $pin.version) {
    throw "Unexpected Inno Setup engine version: $actualVersion"
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $tools 'license.txt') -Destination (Join-Path $OutputDir 'InnoSetup-LICENSE.txt') -Force
# Bake the exact published file names into the uninstaller. A mutable manifest
# or recursive wildcard must never authorize deletion of user-created files.
$ownedScript = Join-Path $OutputDir 'owned-payload.iss'
$ownedLines = [Collections.Generic.List[string]]::new()
$ownedLines.Add('function RemoveOwnedPayload: Boolean;')
$ownedLines.Add('var Base: String;')
$ownedLines.Add('begin')
$ownedLines.Add('  Result := True;')
$ownedLines.Add("  Base := AddBackslash(ExpandConstant('{app}'));")
foreach ($file in Get-ChildItem -LiteralPath $PublishDir -Recurse -File | Sort-Object FullName) {
    $relative = $file.FullName.Substring($PublishDir.Length).TrimStart('\').Replace("'", "''")
    $ownedLines.Add("  Result := RemoveOwnedFile(Base + '$relative') and Result;")
}
$ownedLines.Add('end;')
[IO.File]::WriteAllLines($ownedScript, $ownedLines, [Text.UTF8Encoding]::new($true))
& $compiler "/DAppVersion=$Version" "/DPublishDir=$PublishDir" "/DOutputDir=$OutputDir" "/DOwnedPayloadScript=$ownedScript" "$root\installer\PdfMetaStudio.iss"
if ($LASTEXITCODE) { throw 'EXE installer compilation failed' }
$output = Join-Path $OutputDir "PdfMetaStudio-$Version-win-x64-setup.exe"
if (-not (Test-Path -LiteralPath $output -PathType Leaf)) { throw 'Compiled EXE installer missing' }
Write-Host "Built EXE installer: $output"
