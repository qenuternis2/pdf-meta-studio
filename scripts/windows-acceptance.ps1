param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Pdf, [Parameter(Mandatory)][string]$StressPdf, [Parameter(Mandatory)][string]$OutDir)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
$existed = Test-Path $key
$original = if ($existed) { (Get-ItemProperty -LiteralPath $key -Name AppsUseLightTheme -ErrorAction SilentlyContinue).AppsUseLightTheme } else { $null }
$cases = @()
try {
    if (-not $existed) { New-Item -Path $key -Force | Out-Null }
    foreach ($theme in @('light', 'dark')) {
        New-ItemProperty -LiteralPath $key -Name AppsUseLightTheme -PropertyType DWord -Value $(if ($theme -eq 'light') { 1 } else { 0 }) -Force | Out-Null
        $timer = [Diagnostics.Stopwatch]::StartNew()
        & powershell.exe -NoProfile -File "$PSScriptRoot\gui-smoke.ps1" -Exe $Exe -Pdf $Pdf -OutDir (Join-Path $OutDir $theme) -KeyboardChecks -LayoutChecks -ExpectedTheme $theme
        $cases += @{ case = $theme; exitCode = $LASTEXITCODE; seconds = $timer.Elapsed.TotalSeconds }
        if ($LASTEXITCODE) { throw "$theme keyboard/theme/layout acceptance failed" }
    }
    New-ItemProperty -LiteralPath $key -Name AppsUseLightTheme -PropertyType DWord -Value 1 -Force | Out-Null
    $timer = [Diagnostics.Stopwatch]::StartNew()
    & powershell.exe -NoProfile -File "$PSScriptRoot\gui-smoke.ps1" -Exe $Exe -Pdf $StressPdf -OutDir (Join-Path $OutDir 'stress') -StressChecks
    $cases += @{ case = '10000-tags-1MiB-text'; exitCode = $LASTEXITCODE; seconds = $timer.Elapsed.TotalSeconds }
    if ($LASTEXITCODE) { throw 'Large-data GUI acceptance failed' }
} finally {
    if ($null -ne $original) { New-ItemProperty -LiteralPath $key -Name AppsUseLightTheme -PropertyType DWord -Value $original -Force | Out-Null }
    elseif (Test-Path $key) { Remove-ItemProperty -LiteralPath $key -Name AppsUseLightTheme -ErrorAction SilentlyContinue }
    if (-not $existed -and (Test-Path $key)) { Remove-Item -LiteralPath $key }
    @{ sourceCommit = (git rev-parse HEAD); enginePackageRun = $env:GUI_ENGINE_PACKAGE_RUN; os = [Environment]::OSVersion.VersionString; cases = $cases; limits = 'Hosted Windows automation: observed light/dark backgrounds, keyboard cycles/shortcuts, 640x480 and 1000x700 windows, large-data search/read/save. Physical DPI changes and Narrator speech were not tested. GUI-only workflow runs reuse the recorded native package and do not establish native/MSI acceptance for this source.' } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'results.json') -Encoding utf8
}
