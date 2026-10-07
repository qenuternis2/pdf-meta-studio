param([Parameter(Mandatory)][string]$OutDir)
$ErrorActionPreference = 'Stop'
$env:PSModulePath = (Join-Path $PSHOME 'Modules') + [IO.Path]::PathSeparator + $env:PSModulePath
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
# First-sign-in privacy UI can cover the entire hosted Windows 11 desktop and consume SendKeys.
# Configure this disposable CI user/VM before application automation; never accept privacy defaults.
$policy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\OOBE'
New-Item -Path $policy -Force | Out-Null
New-ItemProperty -Path $policy -Name DisablePrivacyExperience -PropertyType DWord -Value 1 -Force | Out-Null
$stopped = @()
foreach ($name in @('UserOOBEBroker', 'msoobe', 'CloudExperienceHostBroker')) {
    foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
        $stopped += @{ name = $process.ProcessName; id = $process.Id }
        Stop-Process -Id $process.Id -Force
    }
}
foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name = 'WWAHost.exe'")) {
    if ($process.CommandLine -match 'CloudExperienceHost') {
        $stopped += @{ name = 'WWAHost'; id = $process.ProcessId }
        Stop-Process -Id $process.ProcessId -Force
    }
}
Start-Sleep -Seconds 2
@{ sourceCommit = (git rev-parse HEAD); disabledFirstSignInPrivacyExperience = $true; stopped = $stopped; scope = 'Disposable GitHub Actions Windows 11 VM desktop preparation; no privacy choices were accepted.' } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'desktop-preparation.json') -Encoding utf8
