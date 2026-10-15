# Regression: retry an owned DLL after a short, deliberate sharing violation.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'installer-test-common.ps1')
Assert-FreshInstall
$output = Join-Path $TestRoot 'installer-file-lock'
New-Item -ItemType Directory -Path $output | Out-Null
if ((Invoke-Setup $CurrentSetup (Join-Path $output 'install.log')) -ne 0) { throw 'File-lock test setup failed' }
$null = Assert-InstalledPayload
$sentinel = Join-Path $InstallDir 'user-document.pdf'
Copy-Item -LiteralPath (Join-Path $TestRoot 'tests\fixtures\rich.pdf') -Destination $sentinel
$hash = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
$locked = Join-Path $InstallDir 'System.Collections.Immutable.dll'
$stream = [IO.File]::Open($locked, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
$process = $null
$log = Join-Path $output 'uninstall.log'
try {
    $process = Start-Process -FilePath (Join-Path $InstallDir 'unins000.exe') -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + $log + '"')
    ) -PassThru
    $until = [DateTime]::UtcNow.AddSeconds(15)
    $retryObserved = $false
    while ([DateTime]::UtcNow -lt $until -and -not $process.HasExited) {
        if ((Test-Path -LiteralPath $log) -and (Select-String -LiteralPath $log -SimpleMatch 'Retrying owned payload removal' -Quiet)) {
            $retryObserved = $true
            break
        }
        Start-Sleep -Milliseconds 50
    }
    if (-not $retryObserved) { throw 'Uninstaller did not retry the deliberately locked owned file' }
    $stream.Dispose()
    $stream = $null
    if (-not $process.WaitForExit(20000)) { throw 'Uninstaller did not finish after the lock was released' }
    if ($process.ExitCode -ne 0) { throw "File-lock test uninstall failed: $($process.ExitCode)" }
    Assert-UninstalledPayload $log
    if ((Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -ne $hash) { throw 'Uninstaller changed an unowned PDF' }
    Remove-Item -LiteralPath $sentinel -Force
    if (@(Get-ChildItem -LiteralPath $InstallDir -Force).Count -ne 0) { throw 'Unexpected payload remained after lock recovery' }
    Remove-Item -LiteralPath $InstallDir -Force
    @{ status = 'PASS'; version = $SetupVersion; retryObserved = $retryObserved; preservedDocuments = 1
        scope = 'A test-owned handle denied deletion of one installed DLL until the retry was logged; no process was forcibly closed.'
    } | ConvertTo-Json | Set-Content (Join-Path $output 'results.json') -Encoding utf8
    Write-Host 'PASS bounded owned-file retry, complete uninstall and unowned PDF preservation'
} finally {
    if ($stream) { $stream.Dispose() }
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
}
