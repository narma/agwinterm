#nullable enable
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

// Shared by PowerShell entry points and the standalone WebView gate.
// This is a routing guard, not a security boundary against a guest administrator.
public static class HyperVUiIdentity
{
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);

    private static bool IsDefault(IntPtr desktop)
    {
        var name = new StringBuilder(256);
        int needed;
        return desktop != IntPtr.Zero && GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out needed) &&
            name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsGuest()
    {
        if (Environment.GetEnvironmentVariable("AGWINTERM_HYPERV_UI") != "1") return false;
        try
        {
            using (var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
            using (var host = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters"))
            using (var run = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Agwinterm\UiTests"))
            {
                string? id = Environment.GetEnvironmentVariable("AGWINTERM_HYPERV_RUN_ID");
                Guid parsed;
                if (!Guid.TryParse(id, out parsed) || parsed == Guid.Empty ||
                    bios?.GetValue("SystemManufacturer") as string != "Microsoft Corporation" ||
                    bios?.GetValue("SystemProductName") as string != "Virtual Machine" ||
                    run?.GetValue("ActiveRunId") as string != id ||
                    run?.GetValue("UserSid") as string != WindowsIdentity.GetCurrent().User?.Value ||
                    string.IsNullOrEmpty(run?.GetValue("ActiveVmName") as string) ||
                    run?.GetValue("ActiveVmName") as string != host?.GetValue("VirtualMachineName") as string ||
                    Process.GetCurrentProcess().SessionId == 0) return false;
            }
            IntPtr input = OpenInputDesktop(0, false, 1);
            try { return IsDefault(input) && IsDefault(GetThreadDesktop(GetCurrentThreadId())); }
            finally { if (input != IntPtr.Zero) CloseDesktop(input); }
        }
        catch { return false; }
    }
}
