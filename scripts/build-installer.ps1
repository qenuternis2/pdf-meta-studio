param([string]$PublishDir = "$PSScriptRoot\..\dist\PdfMetaStudio")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$props = Get-Content "$root\Directory.Build.props"
$version = $props.Project.PropertyGroup.Version
$toolBase = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$tools = Join-Path $toolBase 'pdfmeta-wix'
if (-not (Test-Path "$tools\wix.exe")) {
    dotnet tool install wix --version 5.0.2 --tool-path $tools
    if ($LASTEXITCODE) { throw 'WiX installation failed' }
}
$source = Join-Path $root 'build\installer\PdfMetaStudio.wxs'
python "$root\installer\generate.py" $PublishDir $source --version $version
if ($LASTEXITCODE) { throw 'Installer generation failed' }
New-Item -ItemType Directory "$root\dist\installer" -Force | Out-Null
& "$tools\wix.exe" build $source -arch x64 -o "$root\dist\installer\PdfMetaStudio-$version-win-x64.msi"
if ($LASTEXITCODE) { throw 'Installer build failed' }
