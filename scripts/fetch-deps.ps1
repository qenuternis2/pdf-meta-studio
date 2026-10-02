# Загружает qpdf, XMP-Toolkit-SDK и Expat по версиям из scripts\deps.lock в worker\external
# и применяет обязательный патч XMP SDK. Повторный запуск безопасен.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$ext = Join-Path $root 'worker\external'
New-Item -ItemType Directory -Force -Path $ext | Out-Null
foreach ($line in Get-Content (Join-Path $root 'scripts\deps.lock')) {
    if ($line -match '^\s*(#|$)') { continue }
    $name, $url, $tag, $commit = $line -split '\s+'
    $dir = Join-Path $ext $name
    if (-not (Test-Path (Join-Path $dir '.git'))) {
        git -c advice.detachedHead=false clone --quiet --depth 1 --branch $tag $url $dir
        if ($LASTEXITCODE) { throw "git clone $name failed" }
    }
    $actual = (git -C $dir rev-parse HEAD).Trim()
    if ($actual -ne $commit) { throw "${name}: expected commit $commit, got $actual" }
    Write-Host "$name $tag $commit OK"
}
$patch = Join-Path $root 'scripts\patches\xmp-keep-translations.patch'
git -C (Join-Path $ext 'xmp') apply --reverse --check $patch 2>$null
if ($LASTEXITCODE -eq 0) { Write-Host 'XMP patch already applied' }
else {
    git -C (Join-Path $ext 'xmp') apply $patch
    if ($LASTEXITCODE) { throw 'git apply failed' }
    Write-Host 'XMP patch applied'
}
