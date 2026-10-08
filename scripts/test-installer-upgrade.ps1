# Execute only in disposable Windows CI; the legacy MSI is never launched on a user's PC.
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or $env:GITHUB_ACTIONS -ne 'true') {
    throw 'Installer upgrade acceptance requires disposable Windows GitHub Actions'
}

$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'dist\PdfMetaStudio'
$installed = Join-Path $env:LOCALAPPDATA 'PdfMetaStudio'
$output = Join-Path $root 'installer-upgrade'
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if ([version]$version -le [version]'0.3.0') { throw 'Upgrade acceptance requires a version newer than 0.3.0' }
$currentMsi = Join-Path $root "dist\installer\PdfMetaStudio-$version-win-x64.msi"
if (-not (Test-Path -LiteralPath $currentMsi -PathType Leaf)) { throw 'Current installer missing' }
if (Test-Path -LiteralPath $installed) { throw 'Upgrade acceptance requires a clean installation directory' }
if (Test-Path -LiteralPath $output) { throw 'Upgrade acceptance requires a fresh evidence directory' }

$baselineUrl = 'https://github.com/qenuternis2/pdf-meta-studio/releases/download/v0.3.0/PdfMetaStudio-0.3.0-win-x64.msi'
$baselineHash = 'cfa7398eb07b8025afc67820bc10f86da16bcd142531253005ef6c8eabacd05a'
$cache = Join-Path $env:RUNNER_TEMP ('pdfmeta-upgrade-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $cache, $output | Out-Null
$baselineMsi = Join-Path $cache 'PdfMetaStudio-0.3.0-win-x64.msi'
Invoke-WebRequest -Uri $baselineUrl -OutFile $baselineMsi
if ((Get-FileHash -LiteralPath $baselineMsi -Algorithm SHA256).Hash -ne $baselineHash) {
    throw 'Legacy MSI SHA-256 mismatch; refusing to install'
}

$msi = New-Object -ComObject WindowsInstaller.Installer
$msiexec = Join-Path $env:SystemRoot 'System32\msiexec.exe'
function Get-MsiInfo([string]$Path) {
    $database = $msi.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $msi, @($Path, 0))
    $info = @{}
    try {
        foreach ($name in @('ProductCode', 'ProductVersion', 'UpgradeCode')) {
            $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database,
                @("SELECT Value FROM Property WHERE Property = '$name'"))
            $record = $null
            try {
                [void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
                $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
                if (-not $record) { throw "MSI property missing: $name" }
                $info[$name] = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1))
            } finally {
                if ($record) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
                [void]$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
            }
        }
    } finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    }
    return [pscustomobject]$info
}
function Get-ProductState([string]$Code) {
    return $msi.GetType().InvokeMember('ProductState', 'GetProperty', $null, $msi, @($Code))
}
function Invoke-Installer([string]$Operation, [string]$Package, [string]$LogName) {
    $log = Join-Path $output $LogName
    $process = Start-Process -FilePath $msiexec -ArgumentList @(
        $Operation, ('"' + $Package + '"'), '/qn', '/norestart', '/l*v', ('"' + $log + '"')
    ) -Wait -PassThru
    if ($process.ExitCode -notin @(0, 3010)) { throw "$LogName failed: $($process.ExitCode)" }
    return $process.ExitCode
}

try {
    $old = Get-MsiInfo $baselineMsi
    $new = Get-MsiInfo $currentMsi
    if ($old.ProductVersion -ne '0.3.0' -or $new.ProductVersion -ne $version) { throw 'Unexpected MSI versions' }
    if ($old.UpgradeCode -ne $new.UpgradeCode -or $old.ProductCode -eq $new.ProductCode) {
        throw 'The new MSI must keep UpgradeCode and change ProductCode'
    }
    if ((Get-ProductState $old.ProductCode) -ne -1 -or (Get-ProductState $new.ProductCode) -ne -1) {
        throw 'Upgrade acceptance refuses to touch a pre-existing registered product'
    }
    $oldExit = Invoke-Installer '/i' $baselineMsi 'baseline-install.log'
    if ((Get-ProductState $old.ProductCode) -ne 5) { throw 'Legacy product is not registered as installed' }

    # Both documents are synthetic: one beside the application and one outside its install tree.
    $inside = Join-Path $installed 'user-sentinel.pdf'
    $outside = Join-Path $output 'user-document.pdf'
    Copy-Item -LiteralPath (Join-Path $root 'tests\fixtures\rich.pdf') -Destination $inside
    Copy-Item -LiteralPath $inside -Destination $outside
    $documentHash = (Get-FileHash -LiteralPath $inside -Algorithm SHA256).Hash

    $upgradeExit = Invoke-Installer '/i' $currentMsi 'upgrade-install.log'
    if ((Get-ProductState $old.ProductCode) -ne -1 -or (Get-ProductState $new.ProductCode) -ne 5) {
        throw 'Upgrade must remove the old product registration and install the new one'
    }
    $installedVersion = $msi.GetType().InvokeMember('ProductInfo', 'GetProperty', $null, $msi,
        @($new.ProductCode, 'VersionString'))
    if ($installedVersion -ne $version) { throw 'Installed product version is stale' }
    $files = @(Get-ChildItem -LiteralPath $publish -Recurse -File)
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($publish.Length).TrimStart('\')
        $destination = Join-Path $installed $relative
        if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { throw "Upgrade payload missing: $relative" }
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) {
            throw "Upgrade retained stale payload: $relative"
        }
    }
    foreach ($document in @($inside, $outside)) {
        if ((Get-FileHash -LiteralPath $document -Algorithm SHA256).Hash -ne $documentHash) {
            throw 'Upgrade modified an unowned document'
        }
    }
    & powershell.exe -NoProfile -File (Join-Path $PSScriptRoot 'gui-smoke.ps1') -Exe (
        Join-Path $installed 'PdfMetaStudio.exe'
    ) -Pdf $outside -OutDir (Join-Path $output 'gui')
    if ($LASTEXITCODE) { throw 'Upgraded application GUI smoke failed' }

    $uninstallExit = Invoke-Installer '/x' $currentMsi 'upgrade-uninstall.log'
    if ((Get-ProductState $new.ProductCode) -ne -1) { throw 'Upgraded product is still registered after uninstall' }
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($publish.Length).TrimStart('\')
        if (Test-Path -LiteralPath (Join-Path $installed $relative)) { throw "Uninstall retained owned payload: $relative" }
    }
    foreach ($document in @($inside, $outside)) {
        if ((Get-FileHash -LiteralPath $document -Algorithm SHA256).Hash -ne $documentHash) {
            throw 'Uninstall removed or modified an unowned document'
        }
    }
    # Remove only the test-owned sentinel, then an empty test-owned installation directory.
    Remove-Item -LiteralPath $inside -Force
    if (@(Get-ChildItem -LiteralPath $installed -Force).Count -ne 0) { throw 'Unexpected remaining installation files' }
    Remove-Item -LiteralPath $installed -Force
    @{
        status = 'PASS'; baselineVersion = $old.ProductVersion; version = $version
        baselineProductCode = $old.ProductCode; productCode = $new.ProductCode; upgradeCode = $new.UpgradeCode
        baselineUrl = $baselineUrl; baselineSha256 = $baselineHash; verifiedPayloadFiles = $files.Count
        baselineExitCode = $oldExit; upgradeExitCode = $upgradeExit; uninstallExitCode = $uninstallExit
        documentSha256 = $documentHash; preservedDocuments = 2
        limits = 'Disposable hosted Windows session; standard-user physical x64/offline upgrade remains untested.'
    } | ConvertTo-Json | Set-Content (Join-Path $output 'results.json') -Encoding utf8
    Write-Host "PASS per-user 0.3.0 -> $version upgrade, exact payload, GUI, document retention and uninstall"
} finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($msi)
}
