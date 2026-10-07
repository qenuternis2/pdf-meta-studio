param([Parameter(Mandatory)][string]$OutDir, [Parameter(Mandatory)][string]$Exe, [switch]$RequireWindows11Arm)
$ErrorActionPreference = 'Stop'
$env:PSModulePath = (Join-Path $PSHOME 'Modules') + [IO.Path]::PathSeparator + $env:PSModulePath
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$os = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$computer = Get-ComputerInfo -Property WindowsProductName, OsArchitecture, OsBuildNumber
$principal = New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AcceptanceDpi {
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
}
'@
$oldContext = [AcceptanceDpi]::SetThreadDpiAwarenessContext([IntPtr](-4))
try { $dpi = [AcceptanceDpi]::GetDpiForSystem(); $screen = [Windows.Forms.Screen]::PrimaryScreen }
finally { [AcceptanceDpi]::SetThreadDpiAwarenessContext($oldContext) | Out-Null }
$stream = [IO.File]::OpenRead([IO.Path]::GetFullPath($Exe))
$reader = New-Object IO.BinaryReader $stream
try {
    $stream.Position = 0x3c
    $peOffset = $reader.ReadInt32()
    $stream.Position = $peOffset
    if ($reader.ReadUInt32() -ne 0x00004550) { throw 'Application is not a PE executable' }
    $machine = $reader.ReadUInt16()
} finally { $reader.Dispose() }
$record = [ordered]@{
    sourceCommit = (git rev-parse HEAD)
    product = $os.Caption
    registryProductName = $computer.WindowsProductName
    build = $os.BuildNumber
    version = $os.Version
    osArchitecture = $os.OSArchitecture
    processorArchitectureCode = $cpu.Architecture
    runnerArchitecture = $env:RUNNER_ARCH
    powershellArchitecture = $env:PROCESSOR_ARCHITECTURE
    applicationMachine = ('0x{0:X4}' -f $machine)
    applicationSha256 = (Get-FileHash -LiteralPath $Exe -Algorithm SHA256).Hash
    elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    interactive = [Environment]::UserInteractive
    desktop = @{ width = $screen.Bounds.Width; height = $screen.Bounds.Height; dpi = $dpi; scalePercent = $dpi * 100 / 96 }
    limits = 'Hosted VM with SDKs/tools installed. x64 application runs under Windows ARM64 emulation. This is not a clean x64 PC or standard-user/non-elevated/offline acceptance. Recorded desktop DPI does not establish scale-change coverage or Narrator speech.'
}
$record | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'host.json') -Encoding utf8
$record | ConvertTo-Json -Depth 5 | Write-Output
if ($RequireWindows11Arm) {
    if ($os.ProductType -ne 1 -or [int]$os.BuildNumber -lt 22000 -or $os.Caption -notmatch 'Windows 11') { throw 'Actual host is not Windows 11 client' }
    if ($cpu.Architecture -ne 12) { throw 'Actual host processor is not ARM64' }
    if ($machine -ne 0x8664) { throw 'Expected the shipped x64 application on ARM64 Windows' }
}
