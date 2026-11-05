using Agwinterm.Core;
using Agwinterm.Pty;

internal static class JournalChecks
{
    public static async Task Run(string directory)
    {
        Environment.SetEnvironmentVariable("AGWINTERM_GHOSTTY_COUNTERS","1");
        if(!GhosttyVt.TryLoad(directory,out var error)) throw new InvalidOperationException(error);
        var conpty=ConPtyApi.Select(true,directory);
        if(!conpty.Bundled) throw new InvalidOperationException("Bundled ConPTY required.");
        string id="agwinterm-journal-test-"+Guid.NewGuid().ToString("N");
        string gateName=@"Local\"+id;
        using var gate=new EventWaitHandle(false,EventResetMode.AutoReset,gateName);
        using var ready=new EventWaitHandle(false,EventResetMode.AutoReset,gateName+"-ready");
        string script=Path.Combine(Path.GetTempPath(),id+".ps1");
        string replyFile=Path.Combine(Path.GetTempPath(),id+".txt");
        int checks=0;
        void Check(bool ok,string message) { if(!ok) throw new InvalidOperationException("FAIL: "+message); Console.WriteLine("PASS: "+message); checks++; }
        bool Wait(Func<bool> predicate)
        {
            var until=DateTime.UtcNow.AddSeconds(15);
            do { if(predicate()) return true; Thread.Sleep(20); } while(DateTime.UtcNow < until);
            return predicate();
        }
        string Text(ServerSession session) { lock(session.SyncRoot) return string.Join("\n",session.Emulator.DumpBuffer()); }
        File.WriteAllText(script,$$"""
        [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
        $gate=[Threading.EventWaitHandle]::OpenExisting('{{gateName}}')
        $ready=[Threading.EventWaitHandle]::OpenExisting('{{gateName}}-ready')
        $esc=[string][char]27
        [Console]::Write(($esc+'[?9001l'+$esc+'[5n'))
        $reply='';$timer=[Diagnostics.Stopwatch]::StartNew()
        while($timer.ElapsedMilliseconds -lt 10000 -and -not $reply.EndsWith('n')) {
            if([Console]::KeyAvailable){$reply += [Console]::ReadKey($true).KeyChar}else{Start-Sleep -Milliseconds 10}
        }
        Set-Content -LiteralPath '{{replyFile}}' -Value $reply.Replace($esc,'ESC')
        [Console]::Write(($esc+'[2J'+$esc+'[HJOURNAL e'+[char]0x0301+[char]0x754c+$esc+'[31'))
        [void]$ready.Set();[void]$gate.WaitOne()
        [Console]::Write(('m'+$esc+'[2;1HDETACHED'+[char]7))
        [void]$ready.Set();[void]$gate.WaitOne()
        $inputText=''
        do { $key=[Console]::ReadKey($true); if($key.Key -ne 'Enter'){$inputText += $key.KeyChar} } while($key.Key -ne 'Enter')
        [Console]::Write(($esc+'[3;1HINPUT='+$inputText+[char]7))
        [void]$ready.Set();[void]$gate.WaitOne()
        exit 7
        """);
        try
        {
            using var host=new PtyHostServer(id,conpty);
            using var client=PtyHostClient.Connect(id);
            Check(client.JournalRevision == 1,"broker advertises explicit journal capability");
            using var backend=new ServerSessionBackend(id,null,replicaCore:"ghostty",journalOwner:true);
            var first=(ServerSession)backend.Create("one",60,10);
            first.SetJournalHost(new Probe());
            await first.StartAsync("powershell.exe",["-NoProfile","-ExecutionPolicy","Bypass","-File",script]);
            Check(ready.WaitOne(15000),"child receives terminal query reply from UI owner");
            Check(File.ReadAllText(replyFile).Trim() == "ESC[0n","terminal query is answered exactly once");
            bool unicode=Wait(()=>Text(first).Contains("JOURNAL é界"));
            if(!unicode) Console.WriteLine("Observed buffer: "+System.Text.Json.JsonSerializer.Serialize(Text(first)));
            Check(unicode,"UI owner parses journal Unicode output");
            Check(GhosttyDiagnostics.Snapshot() is { Owners:1,LegacyReplicas:0 },"one UI owner and zero broker terminals or replicas");
            bool legacyRejected=false;
            try { using var old=client.Attach("one",nativeSnapshot:true); }
            catch(InvalidOperationException ex) when(ex.Message.Contains("journal-capable")) { legacyRejected=true; }
            Check(legacyRejected,"legacy attachment cannot reinterpret journal bytes");
            Check(await first.CheckpointJournalAsync(),"UI snapshot is stored independently by the broker before trimming");
            int? pid=first.ChildProcessId; string ticket=first.CreationTicket;
            first.Detach();
            Check(Wait(()=>GhosttyDiagnostics.Snapshot().Owners == 0),"detach releases the sole native terminal");
            gate.Set(); Check(ready.WaitOne(15000),"broker journals output with no terminal parser attached");
            var probe=new Probe();
            using var second=(ServerSession)backend.Create("one",60,10);
            second.SetJournalHost(probe);
            Check(second.TryAdopt(ticket) && second.ChildProcessId == pid,"replacement owner adopts the same shell");
            Check(Wait(()=>Text(second).Contains("DETACHED")),"replacement replays the exact journal tail");
            lock(second.SyncRoot) Check(second.Emulator.Screen[1,0].FgSpec == ColorSpec.Indexed(1),"checkpoint preserves unfinished CSI across owner replacement");
            Check(probe.Bells == 0,"replay does not repeat a detached bell");
            gate.Set(); second.Write("Z\r"u8);
            Check(ready.WaitOne(15000) && Wait(()=>Text(second).Contains("INPUT=Z")),"replay does not inject a duplicate terminal reply into later input");
            Check(Wait(()=>probe.Bells == 1),"live effects resume after replay barrier");
            second.Resize(72,12);
            Check(Wait(()=>{lock(second.SyncRoot) return ((GhosttyTerminalCore)second.Emulator).Cols == 72;}),"resize arrives as an ordered owner event");
            second.Inject("\u001b[4;1HINJECTED"u8);
            Check(Wait(()=>Text(second).Contains("INJECTED")),"explicit output injection is ordered through the broker journal");
            Check(await second.CheckpointJournalAsync(),"a replacement owner publishes another checkpoint");
            second.Detach();
            using var third=(ServerSession)backend.Create("one",72,12);
            third.SetJournalHost(new Probe());
            Check(third.TryAdopt(ticket) && Wait(()=>Text(third).Contains("INJECTED")),"checkpoint retains injected output after another owner replacement");
            lock(third.SyncRoot) Check(((GhosttyTerminalCore)third.Emulator).Cols == 72,"checkpoint retains ordered resize dimensions");
            gate.Set();
            Check(Wait(()=>third.HasExited) && third.ExitCode == 7,"journal exit follows final output");
        }
        finally { File.Delete(script); File.Delete(replyFile); }
        Check(Wait(()=>GhosttyDiagnostics.Snapshot() is { Owners:0,LegacyReplicas:0,ActiveCaptures:0 }),"journal disposal releases all native owners");
        Console.WriteLine($"{checks} journal checks passed.");
    }
    private sealed class Probe : IHostActions
    {
        public int Bells;
        public (Color Foreground,Color Background)? DefaultColors => null;
        public void CommandFinished(string token,long sequence) { }
        public void Respond(string reply) => throw new InvalidOperationException("Broker response bypassed ownership.");
        public void Bell() => Interlocked.Increment(ref Bells);
        public void Notify(string title,string body) { }
        public void Progress(int state,int value) { }
        public void ClipboardWrite(string text) { }
        public void Unhandled(string kind,string detail) { }
    }
}
