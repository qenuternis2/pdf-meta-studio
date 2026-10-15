$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$installer = (Get-ChildItem "$root\dist\installer\*.msi" | Select-Object -First 1).FullName
if (-not $installer) { throw 'Installer missing' }
$msi = New-Object -ComObject WindowsInstaller.Installer
$database = $msi.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $msi, @($installer, 0))
function Read-MsiValue([string]$Query) {
    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($Query))
    $record = $null
    try {
        [void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if (-not $record) { throw "Installer branding row missing: $Query" }
        return $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1))
    } finally {
        if ($record) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
        [void]$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    }
}
try {
    $icon = Read-MsiValue 'SELECT Value FROM Property WHERE Property = ''ARPPRODUCTICON'''
    if ($icon -ne 'ApplicationIcon.ico' -or
        (Read-MsiValue 'SELECT Name FROM Icon WHERE Name = ''ApplicationIcon.ico''') -ne $icon -or
        (Read-MsiValue 'SELECT Icon_ FROM Shortcut WHERE Shortcut = ''ApplicationShortcut''') -ne $icon) {
        throw 'Installed-app listing and Start menu shortcut must reference the embedded application icon'
    }
    Write-Host 'PASS embedded MSI, installed-app listing and Start menu icon references'
} finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($msi)
}
$installed = Join-Path $env:LOCALAPPDATA 'PdfMetaStudio'
if (Test-Path $installed) { throw 'Installer acceptance requires a clean installation directory' }
$process = Start-Process msiexec.exe -ArgumentList @('/i', "`"$installer`"", '/qn', '/norestart', '/l*v', "`"$root\installer-install.log`"") -Wait -PassThru
if ($process.ExitCode -notin @(0, 3010)) { throw "Installer failed: $($process.ExitCode)" }
foreach ($file in @('PdfMetaStudio.exe','pdfmeta-worker.exe','licenses\MANIFEST.txt','validation\native-worker.json','validation\core-tests.trx')) {
    if (-not (Test-Path (Join-Path $installed $file))) { throw "Installed file missing: $file" }
}
& powershell.exe -NoProfile -File "$root\scripts\gui-smoke.ps1" -Exe "$installed\PdfMetaStudio.exe" -Pdf "$root\tests\fixtures\rich.pdf" -OutDir "$root\installer-smoke"
if ($LASTEXITCODE) { throw 'Installed GUI smoke failed' }
$process = Start-Process msiexec.exe -ArgumentList @('/x', "`"$installer`"", '/qn', '/norestart', '/l*v', "`"$root\installer-uninstall.log`"") -Wait -PassThru
if ($process.ExitCode -notin @(0, 3010)) { throw "Uninstall failed: $($process.ExitCode)" }
if (Test-Path $installed) { throw 'Install directory was not removed after uninstall' }
Write-Host 'PASS per-user install, installed GUI workflow and uninstall'
