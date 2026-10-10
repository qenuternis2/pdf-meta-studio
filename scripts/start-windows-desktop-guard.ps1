param([Parameter(Mandatory)][string]$OutDir)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ARCH -ne 'ARM64') {
    throw 'Desktop guard is restricted to disposable Windows ARM GitHub Actions sessions'
}
New-Item -ItemType Directory $OutDir -Force | Out-Null
# First-logon setup tasks can relaunch while UI checks are running.
Start-Job -ArgumentList (Join-Path $OutDir 'desktop-guard.log') -ScriptBlock {
    param($guardLog)
    while ($true) {
        foreach ($process in @(Get-Process -Name WWAHost, UserOOBEBroker, msoobe, CloudExperienceHostBroker, SystemPropertiesPerformance, wsl, WindowsTerminal -ErrorAction SilentlyContinue)) {
            ('{0:o} stopped {1} {2}' -f [DateTime]::UtcNow, $process.ProcessName, $process.Id) | Add-Content -Path $guardLog
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Milliseconds 100
    }
}
