# Run in a fresh PowerShell process: registry, process discovery and SendKeys are isolated test doubles.
$ErrorActionPreference = 'Stop'
Microsoft.PowerShell.Utility\Add-Type -TypeDefinition @'
namespace System.Windows.Forms {
    public static class SendKeys {
        public static int Calls;
        public static void SendWait(string keys) { Calls++; }
    }
}
'@

function New-Item {
    [CmdletBinding()]
    param([string]$Path, [string]$ItemType, [switch]$Force)
    if ($Path -notlike 'HKLM:*') { [IO.Directory]::CreateDirectory($Path) }
}
function New-ItemProperty {
    [CmdletBinding()]
    param($Path, $Name, $PropertyType, $Value, [switch]$Force)
}
function Get-Process {
    [CmdletBinding()]
    param([string]$Name)
    if ($Name -eq 'wsl') { [pscustomobject]@{ ProcessName = 'wsl'; Id = 2147483647 } }
}
function Stop-Process {
    [CmdletBinding()]
    param([int]$Id, [switch]$Force)
    $testState.StopAttempts++
    if ($testState.Mode -eq 'gone') {
        # Exercise PowerShell's actual process-not-found error rather than inventing an error ID.
        Microsoft.PowerShell.Management\Stop-Process -Id $Id -Force -ErrorAction Stop
    }
    if ($testState.Mode -eq 'denied') { throw [UnauthorizedAccessException]::new('Access-denied test sentinel') }
}
function Start-Sleep {
    [CmdletBinding()]
    param($Seconds, $Milliseconds)
}
function Add-Type {
    [CmdletBinding()]
    param($AssemblyName)
}

$root = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'pdfmeta-desktop-test-' + [guid]::NewGuid().ToString('N'))
$oldActions = $env:GITHUB_ACTIONS
$oldModules = $env:PSModulePath
$prepare = Join-Path $PSScriptRoot '../../scripts/prepare-windows-desktop.ps1'
$testState = [pscustomobject]@{ Mode = ''; StopAttempts = 0 }
try {
    $env:GITHUB_ACTIONS = 'true'
    foreach ($mode in @('success', 'gone', 'denied')) {
        $testState.Mode = $mode
        $testState.StopAttempts = 0
        [Windows.Forms.SendKeys]::Calls = 0
        $output = Join-Path $root $mode
        $caught = $null
        try { & $prepare -OutDir $output } catch { $caught = $_ }
        if ($mode -ne 'denied' -and $caught) { throw $caught }
        if ($testState.StopAttempts -ne 1) { throw 'The expected hosted process was not addressed exactly once' }
        if ($mode -eq 'denied') {
            if (-not $caught -or $caught.Exception.Message -notlike '*Access-denied test sentinel*') {
                throw 'Unexpected process termination errors must propagate'
            }
            if ([Windows.Forms.SendKeys]::Calls -ne 0) { throw 'Preparation continued after an unexpected error' }
        } else {
            if ($caught) { throw $caught }
            if ([Windows.Forms.SendKeys]::Calls -ne 2) { throw 'Desktop preparation did not complete' }
            $record = Get-Content (Join-Path $output 'desktop-preparation.json') -Raw | ConvertFrom-Json
            if ($record.stopped.Count -ne 1) { throw 'Desktop preparation evidence is incomplete' }
        }
        Write-Host "PASS hosted process termination: $mode"
    }
} finally {
    $env:GITHUB_ACTIONS = $oldActions
    $env:PSModulePath = $oldModules
    if (Test-Path $root) { Microsoft.PowerShell.Management\Remove-Item -LiteralPath $root -Recurse -Force }
}
