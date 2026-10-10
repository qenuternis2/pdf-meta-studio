param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Pdf,
      [Parameter(Mandatory)][string]$Worker, [Parameter(Mandatory)][string]$OutDir)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or $env:GITHUB_ACTIONS -ne 'true') { throw 'Disposable Windows GitHub Actions only' }
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
$existed = Test-Path $key
$original = if ($existed) { (Get-ItemProperty -LiteralPath $key -Name AppsUseLightTheme -ErrorAction SilentlyContinue).AppsUseLightTheme } else { $null }
$cases = @()
$desktopGuard = $null
try {
    if ($env:RUNNER_ARCH -eq 'ARM64') {
        $desktopGuard = & (Join-Path $PSScriptRoot 'start-windows-desktop-guard.ps1') -OutDir $OutDir
    }
    if (-not $existed) { New-Item -Path $key -Force | Out-Null }
    foreach ($theme in @('light', 'dark')) {
        New-ItemProperty -LiteralPath $key -Name AppsUseLightTheme -PropertyType DWord -Value $(if ($theme -eq 'light') { 1 } else { 0 }) -Force | Out-Null
        $timer = [Diagnostics.Stopwatch]::StartNew()
        & $Exe $Pdf $Worker (Join-Path $OutDir $theme) $theme
        $cases += @{ theme = $theme; exitCode = $LASTEXITCODE; seconds = $timer.Elapsed.TotalSeconds }
        # Keep collecting both themes; failures are returned after reports/restoration.
    }
} finally {
    if ($desktopGuard) {
        Stop-Job $desktopGuard -ErrorAction SilentlyContinue
        Remove-Job $desktopGuard -Force -ErrorAction SilentlyContinue
    }
    if ($null -ne $original) { New-ItemProperty -LiteralPath $key -Name AppsUseLightTheme -PropertyType DWord -Value $original -Force | Out-Null }
    elseif (Test-Path $key) { Remove-ItemProperty -LiteralPath $key -Name AppsUseLightTheme -ErrorAction SilentlyContinue }
    if (-not $existed -and (Test-Path $key)) { Remove-Item -LiteralPath $key }
    New-Item -ItemType Directory $OutDir -Force | Out-Null
    @{ cases = $cases; sourceCommit = $env:GITHUB_SHA } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'process-results.json') -Encoding utf8
}
if ($cases.Count -ne 2 -or @($cases | Where-Object { $_.exitCode -ne 0 }).Count) { throw 'Additional UI audit found failed checks or execution errors; inspect per-theme results.json' }
