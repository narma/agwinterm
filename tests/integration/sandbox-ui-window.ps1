# Connect the Sandbox viewer on a private host desktop. Never switch the user's
# input desktop; keeping the viewer unminimized preserves guest rendering.
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][guid]$SandboxId
)
$ErrorActionPreference = 'Stop'
if (-not ('SandboxBackgroundViewer' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public sealed class SandboxBackgroundViewer : IDisposable {
    IntPtr desktopHandle, processHandle;
    public uint ProcessId { get; private set; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    struct STARTUPINFO {
        public int cb; public string reserved, desktop, title;
        public int x, y, width, height, charsX, charsY, fill, flags;
        public short show, reservedSize; public IntPtr reservedPtr, input, output, error;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr process, thread; public uint pid, tid; }
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode,
        uint flags, uint access, IntPtr security);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool CreateProcessW(string app, StringBuilder command, IntPtr processSecurity,
        IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory,
        ref STARTUPINFO startup, out PROCESS_INFORMATION process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll", SetLastError=true)] static extern uint WaitForInputIdle(IntPtr process, uint timeout);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr handle, uint code);
    public bool HasExited { get { return WaitForSingleObject(processHandle, 0) == 0; } }
    public uint ExitCode { get { uint code; GetExitCodeProcess(processHandle, out code); return code; } }
    public void Dispose() {
        try {
            // The invisible viewer belongs only to this run. Keep the guest VM for
            // inspection, but do not leave an inaccessible viewer blocking later runs.
            if (processHandle != IntPtr.Zero && !HasExited) {
                TerminateProcess(processHandle, 0);
                WaitForSingleObject(processHandle, 2000);
            }
        } finally {
            if (processHandle != IntPtr.Zero) { CloseHandle(processHandle); processHandle = IntPtr.Zero; }
            if (desktopHandle != IntPtr.Zero) { CloseDesktop(desktopHandle); desktopHandle = IntPtr.Zero; }
        }
    }
    public static SandboxBackgroundViewer Start(string exe, Guid id) {
        string name = "AgwintermSandbox-" + Guid.NewGuid().ToString("N");
        IntPtr desktop = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, 0x10000000, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            var startup = new STARTUPINFO();
            startup.cb = Marshal.SizeOf<STARTUPINFO>();
            startup.desktop = "WinSta0\\" + name;
            PROCESS_INFORMATION process;
            if (!CreateProcessW(exe, new StringBuilder("\"" + exe + "\" --id " + id.ToString()),
                IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref startup, out process))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                if (WaitForInputIdle(process.process, 15000) != 0)
                    throw new InvalidOperationException("Sandbox background viewer did not initialize its desktop.");
                var viewer = new SandboxBackgroundViewer { desktopHandle = desktop, processHandle = process.process, ProcessId = process.pid };
                desktop = IntPtr.Zero;
                process.process = IntPtr.Zero;
                return viewer;
            } catch {
                TerminateProcess(process.process, 1);
                throw;
            } finally {
                CloseHandle(process.thread);
                if (process.process != IntPtr.Zero) CloseHandle(process.process);
            }
        } finally {
            if (desktop != IntPtr.Zero) CloseDesktop(desktop);
        }
    }
}
'@
}
$viewer = [SandboxBackgroundViewer]::Start($Exe, $SandboxId)
Write-Host "Sandbox viewer connected on a private host desktop (process $($viewer.ProcessId))."
$viewer
