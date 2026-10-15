# Read-only diagnostics for disposable installer acceptance; never close lockers.
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or $env:GITHUB_ACTIONS -ne 'true') {
    throw 'Installer lock diagnostics require disposable Windows GitHub Actions'
}
$item = Get-Item -LiteralPath $Path
$loaded = @([AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object {
    if (-not $_.IsDynamic -and $_.Location -eq $item.FullName) { $_.FullName }
})
$acl = Get-Acl -LiteralPath $Path
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class InstallerLockProbe {
    [StructLayout(LayoutKind.Sequential)] struct UniqueProcess {
        public uint Id; public System.Runtime.InteropServices.ComTypes.FILETIME Start;
    }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct ProcessInfo {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=64)] public string Service;
        public uint Type, Status, Session;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet=CharSet.Unicode)] static extern int RmStartSession(out uint session, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint session);
    [DllImport("rstrtmgr.dll", CharSet=CharSet.Unicode)] static extern int RmRegisterResources(uint session, uint files, string[] names, uint apps, IntPtr processes, uint services, string[] serviceNames);
    [DllImport("rstrtmgr.dll")] static extern int RmGetList(uint session, out uint needed, ref uint count, [In,Out] ProcessInfo[] processes, ref uint reboot);
    public static string[] Lockers(string path) {
        uint session; int result=RmStartSession(out session,0,new StringBuilder(33));
        if (result!=0) throw new InvalidOperationException("RmStartSession: "+result);
        try {
            result=RmRegisterResources(session,1,new[]{path},0,IntPtr.Zero,0,null);
            if (result!=0) throw new InvalidOperationException("RmRegisterResources: "+result);
            for (int retry=0;retry<3;retry++) {
                uint needed,count=0,reboot=0;
                result=RmGetList(session,out needed,ref count,null,ref reboot);
                if (result==0) return new string[0];
                if (result!=234) throw new InvalidOperationException("RmGetList: "+result);
                var entries=new ProcessInfo[needed];count=needed;
                result=RmGetList(session,out needed,ref count,entries,ref reboot);
                if (result==234) continue;
                if (result!=0) throw new InvalidOperationException("RmGetList: "+result);
                var found=new string[count];
                for(int i=0;i<count;i++) found[i]=entries[i].Process.Id+":"+entries[i].AppName+":"+entries[i].Service;
                return found;
            }
            throw new InvalidOperationException("Restart Manager process list kept changing");
        } finally { RmEndSession(session); }
    }
}
'@
@{ path = $item.FullName; attributes = "$($item.Attributes)"; owner = $acl.Owner; access = "$($acl.AccessToString)"
    currentProcessId = $PID; loadedInCurrentProcess = $loaded
    lockers = @([InstallerLockProbe]::Lockers($item.FullName))
} | ConvertTo-Json -Depth 5
