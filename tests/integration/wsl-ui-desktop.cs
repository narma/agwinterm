using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

// A private Win32 desktop, not a virtual desktop. Never call SwitchDesktop.
// The suspended worker joins a kill-on-close job before any test code runs.
public sealed class WslUiDesktop : IDisposable {
    IntPtr desktop, process, job;
    public string Name { get; private set; }
    public uint ProcessId { get; private set; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    struct Startup {
        public int cb; public string reserved, desktop, title;
        public int x,y,w,h,cx,cy,fill,flags;
        public short show, reservedSize; public IntPtr reservedPtr,input,output,error;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr process,thread; public uint pid,tid; }
    [StructLayout(LayoutKind.Sequential)] struct Limits {
        public long processTime,jobTime; public uint flags;
        public UIntPtr min,max; public uint count; public UIntPtr affinity; public uint priority,scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimits {
        public Limits basic; public ulong readOps,writeOps,otherOps,readBytes,writeBytes,otherBytes;
        public UIntPtr processMemory,jobMemory,peakProcess,peakJob;
    }
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateDesktopW(string name,IntPtr device,IntPtr mode,uint flags,uint access,IntPtr security);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr handle);
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint tid);
    [DllImport("user32.dll", SetLastError=true)] static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool GetUserObjectInformationW(IntPtr handle,int index,StringBuilder value,uint length,out uint needed);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateProcessW(string app,StringBuilder command,IntPtr ps,IntPtr ts,bool inherit,uint flags,IntPtr env,string cwd,ref Startup startup,out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr CreateJobObjectW(IntPtr security,IntPtr name);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job,int kind,ref ExtendedLimits limits,int length);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint timeout);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr handle,out uint code);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process,uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    static string DesktopName(IntPtr handle) {
        if(handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var text = new StringBuilder(512); uint needed;
        if(!GetUserObjectInformationW(handle,2,text,1024,out needed)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return text.ToString();
    }
    public static string CurrentDesktop { get { return DesktopName(GetThreadDesktop(GetCurrentThreadId())); } }
    public static string InputDesktop { get {
        var handle=OpenInputDesktop(0,false,1);
        try { return DesktopName(handle); } finally { if(handle!=IntPtr.Zero) CloseDesktop(handle); }
    } }
    public static bool IsPrivate(string expected) {
        return expected != null && expected.StartsWith("AgwintermWsl-",StringComparison.Ordinal)
            && CurrentDesktop == expected && InputDesktop != expected;
    }
    public bool HasExited { get { return WaitForSingleObject(process,0)==0; } }
    public uint ExitCode { get { uint code; if(!GetExitCodeProcess(process,out code)) throw new Win32Exception(); return code; } }
    public static WslUiDesktop Start(string exe,string encodedCommand,string cwd,string name) {
        var owner = new WslUiDesktop { Name=name };
        ProcessInfo info = new ProcessInfo();
        try {
            owner.desktop=CreateDesktopW(name,IntPtr.Zero,IntPtr.Zero,0,0x10000000,IntPtr.Zero);
            if(owner.desktop==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            owner.job=CreateJobObjectW(IntPtr.Zero,IntPtr.Zero);
            if(owner.job==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits=new ExtendedLimits(); limits.basic.flags=0x2000; // KILL_ON_JOB_CLOSE, no breakaway
            if(!SetInformationJobObject(owner.job,9,ref limits,Marshal.SizeOf<ExtendedLimits>())) throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup=new Startup { cb=Marshal.SizeOf<Startup>(), desktop="WinSta0\\"+name };
            if(!CreateProcessW(exe,new StringBuilder("\""+exe+"\" -NoProfile -NonInteractive -EncodedCommand "+encodedCommand),IntPtr.Zero,IntPtr.Zero,false,0x08000004,IntPtr.Zero,cwd,ref startup,out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            owner.process=info.process; owner.ProcessId=info.pid;
            if(!AssignProcessToJobObject(owner.job,owner.process)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if(ResumeThread(info.thread)==0xffffffff) throw new Win32Exception(Marshal.GetLastWin32Error());
            return owner;
        } catch {
            if(info.process!=IntPtr.Zero) TerminateProcess(info.process,1);
            owner.Dispose(); throw;
        } finally { if(info.thread!=IntPtr.Zero) CloseHandle(info.thread); }
    }
    public void Dispose() {
        if(job!=IntPtr.Zero) { CloseHandle(job); job=IntPtr.Zero; }
        if(process!=IntPtr.Zero) { WaitForSingleObject(process,5000); CloseHandle(process); process=IntPtr.Zero; }
        if(desktop!=IntPtr.Zero) { CloseDesktop(desktop); desktop=IntPtr.Zero; }
    }
}
