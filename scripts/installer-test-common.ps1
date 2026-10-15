# Shared checks for disposable Windows CI. Never install packages on a developer PC.
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or $env:GITHUB_ACTIONS -ne 'true') {
    throw 'Installer acceptance requires disposable Windows GitHub Actions'
}
$TestRoot = Split-Path -Parent $PSScriptRoot
$PublishedDir = Join-Path $TestRoot 'dist\PdfMetaStudio'
$InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\PdfMetaStudio'
$LegacyInstallDir = Join-Path $env:LOCALAPPDATA 'PdfMetaStudio'
$SetupVersion = ([xml](Get-Content (Join-Path $TestRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$CurrentSetup = Join-Path $TestRoot "dist\installer\PdfMetaStudio-$SetupVersion-win-x64-setup.exe"
$UninstallSubKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{CA630B1F-9153-5830-A5EC-84A95D16534D}_is1'
$SetupRegistration = "Registry::HKEY_CURRENT_USER\$UninstallSubKey"
$ShortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'PDF Meta Studio.lnk'

function Assert-FreshInstall {
    foreach ($path in @($InstallDir, $SetupRegistration, $ShortcutPath)) {
        if (Test-Path -LiteralPath $path) { throw "Acceptance refuses a pre-existing installation: $path" }
    }
    if (-not (Test-Path -LiteralPath $CurrentSetup -PathType Leaf)) { throw 'Current EXE installer missing' }
}

function Invoke-Setup([string]$Exe, [string]$Log, [string[]]$ExtraArguments = @()) {
    $process = Start-Process -FilePath $Exe -ArgumentList (@(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/LANG=english', ('/LOG="' + $Log + '"')
    ) + $ExtraArguments) -Wait -PassThru
    return $process.ExitCode
}

function Assert-InstalledPayload([string]$Source = $PublishedDir) {
    $files = @(Get-ChildItem -LiteralPath $Source -Recurse -File)
    if ($files.Count -eq 0) { throw 'No published payload to verify' }
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($Source.Length).TrimStart('\')
        $destination = Join-Path $InstallDir $relative
        if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { throw "Installed payload missing: $relative" }
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) {
            throw "Installed payload mismatch: $relative"
        }
    }
    return $files.Count
}

function Assert-Registration([string]$ExpectedVersion = $SetupVersion) {
    $entry = Get-ItemProperty -LiteralPath $SetupRegistration
    if ($entry.DisplayVersion -ne $ExpectedVersion -or $entry.Publisher -ne 'PDF Meta Studio') {
        throw 'Unexpected installed-app registration'
    }
    $application = Join-Path $InstallDir 'PdfMetaStudio.exe'
    if (-not ([string]$entry.DisplayIcon).TrimStart('"').StartsWith($application, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installed-app registration does not use the application icon'
    }
    if (Test-Path -LiteralPath "Registry::HKEY_LOCAL_MACHINE\$UninstallSubKey") {
        throw 'Per-user setup unexpectedly created a machine-wide registration'
    }
    if (-not (Test-Path -LiteralPath $ShortcutPath -PathType Leaf)) { throw 'Start menu shortcut missing' }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    try {
        if ($shortcut.TargetPath -ne (Join-Path $InstallDir 'PdfMetaStudio.exe')) { throw 'Shortcut targets a different installation' }
        if (-not $shortcut.IconLocation.StartsWith((Join-Path $InstallDir 'PdfMetaStudio.exe'), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Shortcut does not use the application icon'
        }
    } finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }
}

function Invoke-SetupUninstall([string]$Log) {
    $uninstaller = Join-Path $InstallDir 'unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw 'EXE uninstaller missing' }
    $process = Start-Process -FilePath $uninstaller -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + $Log + '"')
    ) -Wait -PassThru
    $exitCode = $process.ExitCode
    if ($exitCode -ne 0) { throw "EXE uninstall failed: $exitCode" }
    Assert-UninstalledPayload $Log
    return $exitCode
}

function Assert-UninstalledPayload([string]$Log) {
    $uninstaller = Join-Path $InstallDir 'unins000.exe'
    # Inno's original process exits before its temporary second phase removes
    # unins000.exe and finishes directory cleanup. Wait for the final log marker.
    $until = [DateTime]::UtcNow.AddSeconds(20)
    $finished = $false
    do {
        $finished = -not (Test-Path -LiteralPath $uninstaller) -and
            (Test-Path -LiteralPath $Log) -and
            (Select-String -LiteralPath $Log -SimpleMatch 'Log closed.' -Quiet)
        if ($finished) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    if (-not $finished) { throw 'EXE uninstaller did not finish its second phase' }
    foreach ($file in Get-ChildItem -LiteralPath $PublishedDir -Recurse -File) {
        $relative = $file.FullName.Substring($PublishedDir.Length).TrimStart('\')
        if (Test-Path -LiteralPath (Join-Path $InstallDir $relative)) {
            & (Join-Path $PSScriptRoot 'installer-lock-diagnostics.ps1') -Path (Join-Path $InstallDir $relative) |
                Tee-Object -FilePath ($Log + '.diagnostics.json') | Write-Host
            throw "Uninstall retained owned payload: $relative"
        }
    }
    if ((Test-Path -LiteralPath $SetupRegistration) -or (Test-Path -LiteralPath $ShortcutPath)) {
        throw 'Uninstall retained registration or shortcut'
    }
}

function Invoke-InstalledGui([string]$Pdf, [string]$OutDir) {
    & powershell.exe -NoProfile -File (Join-Path $PSScriptRoot 'gui-smoke.ps1') -Exe (
        Join-Path $InstallDir 'PdfMetaStudio.exe'
    ) -Pdf $Pdf -OutDir $OutDir
    if ($LASTEXITCODE) { throw 'Installed EXE application GUI smoke failed' }
}
