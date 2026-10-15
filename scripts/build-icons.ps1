# Optional artwork maintenance; normal builds consume the checked-in ICO.
# Requires ImageMagick 7. No rasterization takes place at application runtime.
$ErrorActionPreference = 'Stop'
$assets = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/PdfMetaStudio.App/Assets'
$null = Get-Command magick -ErrorAction Stop
$sizes = @(16, 20, 24, 28, 32, 36, 40, 48, 64, 72, 80, 96, 128, 192, 256)
$arguments = @()
foreach ($size in $sizes) {
    $source = if ($size -le 28) { 'app-small.png' } else { 'app.png' }
    # Resample each frame directly from its master, never from a smaller frame.
    $arguments += @('(', (Join-Path $assets $source), '-filter', 'Lanczos',
        '-resize', "${size}x${size}", '-strip', '-depth', '8', ')')
}
$arguments += (Join-Path $assets 'app.ico')
& magick @arguments
if ($LASTEXITCODE) { throw 'Application icon encoding failed' }
