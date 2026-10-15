param([Parameter(Mandatory)][string]$SetupExe, [Parameter(Mandatory)][string]$OutDir)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or $env:GITHUB_ACTIONS -ne 'true') {
    throw 'Interactive setup acceptance requires disposable Windows GitHub Actions'
}
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class SetupWizardProbe {
    public sealed class Node { public IntPtr Handle; public string Text; public string Class; }
    delegate bool Callback(IntPtr h, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(Callback cb, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, Callback cb, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint id);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    static Node Read(IntPtr h) {
        var text = new StringBuilder(2048); var cls = new StringBuilder(256);
        GetWindowText(h, text, text.Capacity); GetClassName(h, cls, cls.Capacity);
        return new Node { Handle = h, Text = text.ToString().Replace("&", "").Trim(), Class = cls.ToString() };
    }
    public static List<Node> Windows(int[] ids) {
        var result = new List<Node>(); var allowed = new HashSet<int>(ids);
        EnumWindows(delegate(IntPtr h, IntPtr data) {
            uint id; GetWindowThreadProcessId(h, out id);
            if (allowed.Contains((int)id) && IsWindowVisible(h)) result.Add(Read(h));
            return true;
        }, IntPtr.Zero);
        return result;
    }
    public static List<Node> Children(IntPtr parent) {
        var result = new List<Node>();
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr data) {
            if (IsWindowVisible(h)) result.Add(Read(h)); return true;
        }, IntPtr.Zero);
        return result;
    }
    public static void Click(Node button) {
        if (!button.Class.Contains("Button")) throw new InvalidOperationException("Expected a native button");
        if (!PostMessage(button.Handle, 0x00F5, IntPtr.Zero, IntPtr.Zero)) throw new InvalidOperationException("BM_CLICK failed");
    }
}
'@
function Capture([string]$Name) {
    $bounds = [Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bitmap = New-Object Drawing.Bitmap $bounds.Width, $bounds.Height
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($bounds.Location, [Drawing.Point]::Empty, $bounds.Size)
        $bitmap.Save((Join-Path $OutDir $Name))
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}
$guard = $null
$process = $null
$ids = New-Object 'System.Collections.Generic.HashSet[int]'
try {
    if ($env:RUNNER_ARCH -eq 'ARM64') { $guard = & (Join-Path $PSScriptRoot 'start-windows-desktop-guard.ps1') -OutDir $OutDir }
    $process = Start-Process -FilePath $SetupExe -ArgumentList @(
        '/SP-', '/NORESTART', '/LANG=english', ('/LOG="' + (Join-Path $OutDir 'wizard.log') + '"')
    ) -PassThru
    $null = $ids.Add($process.Id)
    $until = [DateTime]::UtcNow.AddSeconds(45)
    $wizard = $null
    while (-not $wizard -and [DateTime]::UtcNow -lt $until) {
        # Setup's loader owns a child .tmp process; accept only this process tree.
        $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId)
        for ($level = 0; $level -lt 3; $level++) {
            foreach ($item in $all) { if ($ids.Contains([int]$item.ParentProcessId)) { $null = $ids.Add([int]$item.ProcessId) } }
        }
        $wizard = [SetupWizardProbe]::Windows([int[]]@($ids)) | Where-Object { $_.Class -eq 'TWizardForm' } | Select-Object -First 1
        if (-not $wizard) { Start-Sleep -Milliseconds 300 }
    }
    if (-not $wizard -or $wizard.Text -notlike '*PDF Meta Studio*') { throw 'Visible setup wizard did not appear' }
    $null = [SetupWizardProbe]::SetForegroundWindow($wizard.Handle)
    Start-Sleep -Milliseconds 300
    Capture 'setup-welcome.png'
    $pages = @()
    $ready = $false
    for ($page = 0; $page -lt 6; $page++) {
        $children = @([SetupWizardProbe]::Children($wizard.Handle))
        $pages += (($children | Where-Object { $_.Text } | ForEach-Object { $_.Text }) -join ' | ')
        $install = $children | Where-Object { $_.Class -like '*Button*' -and $_.Text -eq 'Install' } | Select-Object -First 1
        if ($install) { $ready = $true; break }
        $next = $children | Where-Object { $_.Class -like '*Button*' -and $_.Text -match '^Next\b' } | Select-Object -First 1
        if (-not $next) { throw 'Setup wizard has no usable Next button' }
        [SetupWizardProbe]::Click($next)
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'Setup wizard did not reach Ready to Install' }
    Capture 'setup-ready.png'
    $cancel = [SetupWizardProbe]::Children($wizard.Handle) | Where-Object { $_.Class -like '*Button*' -and $_.Text -eq 'Cancel' } | Select-Object -First 1
    if (-not $cancel) { throw 'Setup wizard Cancel button missing' }
    [SetupWizardProbe]::Click($cancel)
    $until = [DateTime]::UtcNow.AddSeconds(20)
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $until) {
        foreach ($window in [SetupWizardProbe]::Windows([int[]]@($ids))) {
            $yes = [SetupWizardProbe]::Children($window.Handle) | Where-Object { $_.Class -like '*Button*' -and $_.Text -eq 'Yes' } | Select-Object -First 1
            if ($yes) { [SetupWizardProbe]::Click($yes) }
        }
        Start-Sleep -Milliseconds 200
    }
    if (-not $process.HasExited) { throw 'Setup wizard did not exit after cancellation' }
    $process.WaitForExit()
    if ($process.ExitCode -ne 2) { throw "Unexpected wizard cancellation exit: $($process.ExitCode)" }
    @{ status = 'PASS'; title = $wizard.Text; readyReached = $ready; pages = $pages; cancelExitCode = $process.ExitCode } |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'wizard-results.json') -Encoding utf8
    Write-Host 'PASS visible EXE wizard, Next navigation, Ready to Install and cancellation'
} finally {
    foreach ($id in $ids) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
    if ($guard) { Stop-Job $guard; Remove-Job $guard -Force }
}
