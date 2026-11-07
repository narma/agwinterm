using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Porta.Pty;

namespace Agwinterm.Pty;

/// <summary>
/// A ConPTY connection agwinterm creates itself, for the two things Porta.Pty cannot do. Porta.Pty
/// binds <c>CreatePseudoConsole</c> to kernel32, so a session on the shipped conpty.dll
/// (<see cref="ConPtyApi"/>, #339) is created here. And it only calls plain <c>CreateProcess</c>, so
/// a de-elevated session is too: its child shell runs at the interactive user's <b>Medium</b>
/// integrity, spawned from an <b>elevated</b> agwinterm. That is the one direction Windows allows —
/// dropping privileges, never raising them — so an elevated window can host both admin and normal
/// sessions. It derives a Medium-integrity primary token from THIS process's own token (SAFER:
/// SaferCreateLevel + SaferComputeTokenFromLevel, then the integrity label) and launches via
/// <c>CreateProcessAsUserW</c> — <c>CreateProcessWithTokenW</c> rejects the pseudoconsole attribute
/// (error 87) — so no extra privilege is needed: the token is a restricted copy of the caller's.
/// </summary>
internal sealed class ConPtyConnection : IPtyConnection
{
    private readonly ConPtyApi? _api;
    private IntPtr _hPC;
    private IntPtr _hProcess;
    private IntPtr _hThread;
    private readonly FileStream _reader;
    private readonly FileStream _writer;

    public Stream ReaderStream => _reader;
    public Stream WriterStream => _writer;
    public int Pid { get; }
    public int ExitCode { get { GetExitCodeProcess(_hProcess, out int c); return c; } }
#pragma warning disable CS0067 // required by IPtyConnection; TerminalSession uses WaitForExit instead
    public event EventHandler<PtyExitedEventArgs>? ProcessExited;
#pragma warning restore CS0067

    private ConPtyConnection(ConPtyApi? api, IntPtr hPC, IntPtr hProcess, IntPtr hThread,
        FileStream reader, FileStream writer, int pid)
    {
        _api = api; _hPC = hPC; _hProcess = hProcess; _hThread = hThread;
        _reader = reader; _writer = writer; Pid = pid;
    }

    public bool WaitForExit(int milliseconds)
        // TerminalSession drives exit via this call, not the event; ProcessExited is left unraised.
        => WaitForSingleObject(_hProcess, milliseconds < 0 ? 0xFFFFFFFF : (uint)milliseconds) == 0;

    public void Resize(int cols, int rows)
    {
        if (_hPC != IntPtr.Zero) _api!.Resize(_hPC, (short)cols, (short)rows);
    }

    public void Kill()
    {
        if (_hProcess != IntPtr.Zero) TerminateProcess(_hProcess, 1);
    }

    public void Dispose()
    {
        try { _writer.Dispose(); } catch { }
        try { _reader.Dispose(); } catch { }
        if (_hPC != IntPtr.Zero) { _api!.Close(_hPC); _hPC = IntPtr.Zero; }
        if (_hThread != IntPtr.Zero) { CloseHandle(_hThread); _hThread = IntPtr.Zero; }
        if (_hProcess != IntPtr.Zero) { CloseHandle(_hProcess); _hProcess = IntPtr.Zero; }
    }

    /// <summary>Spawn a Windows process with the supplied environment and optional reduced token.
    /// A console API creates a pseudoconsole. The WSL launcher passes null to inherit only its
    /// redirected stdin and output pipe handles.</summary>
    public static ConPtyConnection Spawn(ConPtyApi? api, string commandLine, string? cwd,
        IEnumerable<KeyValuePair<string, string>>? environment, int cols, int rows, bool deElevate)
    {
        string prefix = deElevate ? "de-elevation" : api is null ? "WSL pipes" : "conpty";
        IntPtr inPipeRead = IntPtr.Zero, inPipeWrite = IntPtr.Zero;
        IntPtr outPipeRead = IntPtr.Zero, outPipeWrite = IntPtr.Zero;
        IntPtr hPC = IntPtr.Zero, attrList = IntPtr.Zero, token = IntPtr.Zero, inherited = IntPtr.Zero;
        try
        {
            if (!CreatePipe(out inPipeRead, out inPipeWrite, IntPtr.Zero, 0)) throw Fail("CreatePipe(in)", prefix);
            if (!CreatePipe(out outPipeRead, out outPipeWrite, IntPtr.Zero, 0)) throw Fail("CreatePipe(out)", prefix);

            var siEx = new STARTUPINFOEX();
            siEx.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            siEx.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
            {
                var failure = Fail("InitializeProcThreadAttributeList", prefix);
                Marshal.FreeHGlobal(attrList); attrList = IntPtr.Zero;
                throw failure;
            }
            if (api is null)
            {
                // WSL uses redirected binary streams, including launcher diagnostics.
                // Inherit only this launch's child ends, even during concurrent spawns.
                siEx.StartupInfo.hStdInput = inPipeRead;
                siEx.StartupInfo.hStdOutput = siEx.StartupInfo.hStdError = outPipeWrite;
                inherited = Marshal.AllocHGlobal(2 * IntPtr.Size);
                Marshal.WriteIntPtr(inherited, inPipeRead);
                Marshal.WriteIntPtr(inherited, IntPtr.Size, outPipeWrite);
                if (!SetHandleInformation(inPipeRead, 1, 1) || !SetHandleInformation(outPipeWrite, 1, 1)
                    || !UpdateProcThreadAttribute(attrList, 0, 0x20002 /* HANDLE_LIST */, inherited,
                        (IntPtr)(2 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero)) throw Fail("redirect WSL pipes", prefix);
            }
            else
            {
                int hr = api.Create((short)cols, (short)rows, inPipeRead, outPipeWrite, out hPC);
                if (hr != 0) throw new InvalidOperationException($"CreatePseudoConsole failed (0x{hr:x8})");
                if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw Fail("UpdateProcThreadAttribute", prefix);
            }
            siEx.lpAttributeList = attrList;

            var pi = new PROCESS_INFORMATION();
            var cmd = new string(commandLine.ToCharArray());   // mutable buffer for CreateProcess*
            string? dir = string.IsNullOrEmpty(cwd) ? null : cwd;
            string? block = environment is null ? null : EnvironmentBlock(environment);
            int flags = EXTENDED_STARTUPINFO_PRESENT | (api is null ? 0x08000000 /* CREATE_NO_WINDOW */ : 0) | (block is null ? 0 : CREATE_UNICODE_ENVIRONMENT);
            if (deElevate)
            {
                // A Medium-integrity token derived from OUR (elevated) token via SAFER. Because it's a
                // restricted version of the caller's own token, CreateProcessAsUserW doesn't need
                // SeAssignPrimaryTokenPrivilege (which admins lack). CreateProcessWithTokenW can't be used —
                // it rejects the pseudoconsole attribute (error 87).
                EnablePrivilege("SeIncreaseQuotaPrivilege");
                EnablePrivilege("SeAssignPrimaryTokenPrivilege");
                token = GetDeElevatedToken();
                if (!CreateProcessAsUserW(token, null, cmd, IntPtr.Zero, IntPtr.Zero, api is null, flags,
                    block, dir, ref siEx, out pi)) throw Fail("CreateProcessAsUserW", prefix);
            }
            else if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, api is null, flags, block, dir, ref siEx, out pi))
                throw Fail("CreateProcessW", prefix);

            CloseHandle(inPipeRead); inPipeRead = IntPtr.Zero;
            CloseHandle(outPipeWrite); outPipeWrite = IntPtr.Zero;
            DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); attrList = IntPtr.Zero;

            var writer = new FileStream(new SafeFileHandle(inPipeWrite, ownsHandle: true), FileAccess.Write);
            var reader = new FileStream(new SafeFileHandle(outPipeRead, ownsHandle: true), FileAccess.Read);
            inPipeWrite = IntPtr.Zero; outPipeRead = IntPtr.Zero;   // owned by the streams now

            return new ConPtyConnection(api, hPC, pi.hProcess, pi.hThread, reader, writer, pi.dwProcessId);
        }
        catch
        {
            if (inPipeRead != IntPtr.Zero) CloseHandle(inPipeRead);
            if (inPipeWrite != IntPtr.Zero) CloseHandle(inPipeWrite);
            if (outPipeRead != IntPtr.Zero) CloseHandle(outPipeRead);
            if (outPipeWrite != IntPtr.Zero) CloseHandle(outPipeWrite);
            if (hPC != IntPtr.Zero) api!.Close(hPC);
            if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
            throw;
        }
        finally { if (token != IntPtr.Zero) CloseHandle(token); Marshal.FreeHGlobal(inherited); }
    }

    /// <summary>A CreateProcessW Unicode environment block: NAME=value strings sorted by name, each
    /// NUL-terminated, then one more NUL.</summary>
    private static string EnvironmentBlock(IEnumerable<KeyValuePair<string, string>> environment)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var kv in environment.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        return sb.Append('\0').ToString();
    }

    /// <summary>Derive a Medium-integrity "normal user" primary token from this (elevated) process's own
    /// token via the SAFER API — the standard self-de-elevation technique.</summary>
    private static IntPtr GetDeElevatedToken()
    {
        if (!SaferCreateLevel(SAFER_SCOPEID_USER, SAFER_LEVELID_NORMALUSER, SAFER_LEVEL_OPEN, out IntPtr level, IntPtr.Zero))
            throw Fail("SaferCreateLevel");
        try
        {
            if (!SaferComputeTokenFromLevel(level, IntPtr.Zero, out IntPtr token, 0, IntPtr.Zero))
                throw Fail("SaferComputeTokenFromLevel");
            // SAFER strips admin privileges/group but leaves the integrity level HIGH — drop it to Medium
            // explicitly, or the child still runs elevated-in-all-but-name.
            SetMediumIntegrity(token);
            return token;
        }
        finally { SaferCloseLevel(level); }
    }

    /// <summary>Lower a token's mandatory integrity level to Medium (S-1-16-8192).</summary>
    private static void SetMediumIntegrity(IntPtr token)
    {
        if (!ConvertStringSidToSid("S-1-16-8192", out IntPtr sid)) throw Fail("ConvertStringSidToSid");
        try
        {
            var label = new TOKEN_MANDATORY_LABEL { Label = new SID_AND_ATTRIBUTES { Sid = sid, Attributes = SE_GROUP_INTEGRITY } };
            uint size = (uint)(Marshal.SizeOf<TOKEN_MANDATORY_LABEL>() + GetLengthSid(sid));
            if (!SetTokenInformation(token, TokenIntegrityLevel, ref label, size)) throw Fail("SetTokenInformation(integrity)");
        }
        finally { LocalFree(sid); }
    }

    /// <summary>Best-effort enable a privilege on this process's token (no-op if absent).</summary>
    private static void EnablePrivilege(string name)
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr tok)) return;
            try
            {
                if (!LookupPrivilegeValue(null, name, out LUID luid)) return;
                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(tok); }
        }
        catch { }
    }

    private static InvalidOperationException Fail(string what, string prefix = "de-elevation") =>
        new($"{prefix}: {what} failed (Win32 error {Marshal.GetLastWin32Error()})");

    // ---- P/Invoke ----
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000, CREATE_UNICODE_ENVIRONMENT = 0x00000400, STARTF_USESTDHANDLES = 0x00000100;
    private const uint TOKEN_QUERY = 0x0008, TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint SE_PRIVILEGE_ENABLED = 0x0002, SE_GROUP_INTEGRITY = 0x0020;
    private const int TokenIntegrityLevel = 25;
    private const uint SAFER_SCOPEID_USER = 2, SAFER_LEVELID_NORMALUSER = 0x20000, SAFER_LEVEL_OPEN = 1;

    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TOKEN_PRIVILEGES { public int PrivilegeCount; public LUID Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct SID_AND_ATTRIBUTES { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct TOKEN_MANDATORY_LABEL { public SID_AND_ATTRIBUTES Label; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, int nSize);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr h, out int code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr prev, IntPtr ret);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr prevState, IntPtr returnLength);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SaferCreateLevel(uint scopeId, uint levelId, uint openFlags, out IntPtr levelHandle, IntPtr reserved);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SaferComputeTokenFromLevel(IntPtr levelHandle, IntPtr inAccessToken, out IntPtr outAccessToken, uint flags, IntPtr reserved);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SaferCloseLevel(IntPtr levelHandle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr token, int cls, ref TOKEN_MANDATORY_LABEL info, uint length);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool ConvertStringSidToSid(string sid, out IntPtr pSid);
    [DllImport("advapi32.dll")] private static extern uint GetLengthSid(IntPtr pSid);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? appName, string commandLine, IntPtr procAttrs, IntPtr threadAttrs,
        bool inherit, int creationFlags, string? environment, string? currentDir, ref STARTUPINFOEX startupInfo, out PROCESS_INFORMATION processInfo);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string? appName, string commandLine, IntPtr procAttrs, IntPtr threadAttrs,
        bool inherit, int creationFlags, string? environment, string? currentDir, ref STARTUPINFOEX startupInfo, out PROCESS_INFORMATION processInfo);
}
