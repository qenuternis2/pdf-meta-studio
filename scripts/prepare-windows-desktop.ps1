param([Parameter(Mandatory)][string]$OutDir)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Desktop preparation is restricted to disposable GitHub Actions VMs' }
$env:PSModulePath = (Join-Path $PSHOME 'Modules') + [IO.Path]::PathSeparator + $env:PSModulePath
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
# First-sign-in privacy UI can cover the entire hosted Windows 11 desktop and consume SendKeys.
# Configure this disposable CI user/VM before application automation; never accept privacy defaults.
$policy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\OOBE'
New-Item -Path $policy -Force | Out-Null
New-ItemProperty -Path $policy -Name DisablePrivacyExperience -PropertyType DWord -Value 1 -Force | Out-Null
$stopped = @()
foreach ($name in @('WWAHost', 'UserOOBEBroker', 'msoobe', 'CloudExperienceHostBroker', 'SystemPropertiesPerformance', 'StartMenuExperienceHost', 'wsl', 'WindowsTerminal')) {
    foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
        $stopped += @{ name = $process.ProcessName; id = $process.Id }
        try { Stop-Process -Id $process.Id -Force -ErrorAction Stop }
        catch {
            # A setup process can exit between discovery and termination; other failures remain fatal.
            if ($_.FullyQualifiedErrorId -notlike 'NoProcessFoundForGivenId,*') { throw }
        }
    }
}
Start-Sleep -Seconds 2
Add-Type -AssemblyName System.Windows.Forms
[Windows.Forms.SendKeys]::SendWait('{ESC}')
Start-Sleep -Milliseconds 300
[Windows.Forms.SendKeys]::SendWait('{ESC}')
@{ sourceCommit = (git rev-parse HEAD); disabledFirstSignInPrivacyExperience = $true; stopped = $stopped; scope = 'Disposable GitHub Actions Windows 11 VM desktop preparation; no privacy choices were accepted.' } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'desktop-preparation.json') -Encoding utf8
