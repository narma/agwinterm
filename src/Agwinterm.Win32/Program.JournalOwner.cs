using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Agwinterm.Pty;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

internal partial class Program
{
    private volatile bool _journalWindowParked;
    private static Mutex? _journalOwnerMutex;

    private static bool ActivateExistingJournalOwner()
    {
        if(Environment.GetEnvironmentVariable("AGWINTERM_JOURNAL_OWNER") != "1" ||
            _config.EmulatorCore != "ghostty" || _config.SessionHost != "server") return false;
        string identity=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(_argPipe ?? _appId)));
        _journalOwnerMutex=new Mutex(true,@"Local\AgwintermJournalOwner-"+identity,out bool created);
        if(created) return false;
        Perf("journal reopen: existing owner");
        // Ordinary reopen reuses the parser process. Never start another owner
        // merely because the existing process is still initializing its window.
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe=new NamedPipeClientStream(".",_argPipe ?? _appId,PipeDirection.InOut,PipeOptions.Asynchronous);
        pipe.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
        Perf("journal reopen: connected");
        byte[] request=Encoding.UTF8.GetBytes("{\"cmd\":\"window.select\",\"target\":\"active\"}\n");
        pipe.WriteAsync(request,timeout.Token).AsTask().GetAwaiter().GetResult();
        using var reader=new StreamReader(pipe,Encoding.UTF8,false,4096,leaveOpen:true);
        string? reply=reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
        Perf("journal reopen: reply "+reply);
        using var json=JsonDocument.Parse(reply ?? throw new IOException("Owner did not answer reopen."));
        if(!json.RootElement.GetProperty("ok").GetBoolean()) throw new IOException("Existing journal owner refused reopen.");
        Perf("journal reopen: complete");
        return true;
    }

    private bool ParkJournalOwnerWindow()
    {
        if(_updateQuitting || _isQuickWindow) return false;
        lock(_windowIndex) if(_byId.Count != 1) return false;
        if(!AllSessions().SelectMany(s=>s.AllPanes).Any(p=>p.S is ServerSession { UsesJournal:true,HasExited:false })) return false;
        CloseWindowQuestion(); CloseWindowPicker();
        SaveState(captureCommands:false);
        _journalWindowParked=true;
        KillTimer(_hwnd,(IntPtr)RedrawTimer); _redrawTimerArmed=false;
        ShowWindow(_hwnd,0);
        return true;
    }
}
