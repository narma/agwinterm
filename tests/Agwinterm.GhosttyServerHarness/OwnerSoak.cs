using System.Diagnostics;
using System.Globalization;
using Agwinterm.Core;
using Agwinterm.Pty;

// Headless client of the production owner process. Latency ends at decoded
// viewport availability; separate UI runs measure actual EndDraw.
internal static class OwnerSoak
{
    public static async Task Run(string directory,int count,int minutes,string report)
    {
        if(count is not (1 or 8 or 32) || minutes is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(count));
        string appId="soak-"+Guid.NewGuid().ToString("N");
        using var backend=(ServerSessionBackend)SessionBackends.Resolve("server",appId,
            Path.Combine(directory,"Agwinterm.Win32.exe"),"bundled");
        var slots=new ServerSession[count];
        var seq=new int[count];
        using var samples=new StreamWriter(report){ AutoFlush=true };
        samples.WriteLine("seconds,slot,sequence,echoMs,hostPrivate,clientPrivate,hostHandles,clientHandles,hostCpuMs,clientCpuMs");
        using var self=Process.GetCurrentProcess();
        try
        {
            for(int i=0;i<count;i++)
            {
                var session=slots[i]=(ServerSession)backend.Create($"slot-{i}",80,16);
                // Use the guest's staged runtime. Fresh login environments need
                // not contain DOTNET_ROOT and the VM does not require an SDK.
                string dotnet=Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT")
                    ?? throw new InvalidOperationException("Guest runtime missing"),"dotnet.exe");
                await session.StartAsync(dotnet,[Path.Combine(directory,"echo-fixture","Agwinterm.EchoFixture.dll"),"soak"],freshEnv:false);
                await WaitFor(session,"SOAK_READY");
            }
            using var admin=PtyHostClient.Connect(appId+"-owner-v1");
            using var host=Process.GetProcessById(checked((int)admin.HostPid));
            var duration=Stopwatch.StartNew();
            for(int turn=0;duration.Elapsed < TimeSpan.FromMinutes(minutes);turn++)
            {
                int i=turn%count;
                var s=slots[i];
                int n=++seq[i];
                if(n%12 == 2) s.Resize(n%24 == 2 ? 72 : 80,16);
                long start=Stopwatch.GetTimestamp();
                if(s.SubmitInput(new TerminalInput.Key(13,1,0,"",0,0,13,0,28)) != TerminalInputAcceptance.Accepted)
                    throw new IOException($"Input closed in slot {i}");
                string marker=$"SOAK_{n:D8}!";
                var identity=await WaitFor(s,marker);
                double echo=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if(n%18 == 3)
                {
                    var q=new TerminalQuery(identity.Incarnation,identity.AttachmentEpoch,identity.TerminalGeneration,
                        "search",Text:marker,ResultLimit:5);
                    var found=await s.Presentation!.QueryAsync(q,default);
                    if(found.Status != "ok" || found.Matches?.Count != 1)
                        throw new IOException($"Search did not find exact marker in slot {i}: {found.Status}");
                }
                if(n%24 == 4)
                {
                    string ticket=s.CreationTicket;
                    int? pid=s.ChildProcessId;
                    s.Detach();
                    s=slots[i]=(ServerSession)backend.Create($"slot-{i}",80,16);
                    if(!s.TryAdopt(ticket) || pid is null || s.ChildProcessId != pid)
                        throw new IOException($"Lost child on reattach in slot {i}");
                    await WaitFor(s,marker);
                }
                self.Refresh();host.Refresh();
                samples.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{duration.Elapsed.TotalSeconds:F3},{i},{n},{echo:F4},{host.PrivateMemorySize64},{self.PrivateMemorySize64},{host.HandleCount},{self.HandleCount},{host.TotalProcessorTime.TotalMilliseconds:F1},{self.TotalProcessorTime.TotalMilliseconds:F1}"));
                await Task.Delay(25);
            }
            Console.WriteLine($"PASS {duration.Elapsed} at {count} sessions, {seq.Sum()} correlated echoes.");
        }
        finally
        {
            foreach(var s in slots) s?.Dispose();
            if(PtyHostClient.IsRunning(appId+"-owner-v1"))
            {
                using var admin=PtyHostClient.Connect(appId+"-owner-v1");
                admin.Shutdown();
            }
        }
    }

    internal static async Task<TerminalFrameIdentity> WaitFor(ServerSession session,string marker,bool acknowledge=true)
    {
        long start=Stopwatch.GetTimestamp();
        while(Stopwatch.GetElapsedTime(start)<TimeSpan.FromSeconds(15))
        {
            TerminalFrameIdentity? id;
            bool found;
            lock(session.SyncRoot)
            {
                var view=(PresentedTerminalView)session.Emulator;
                using var frame=view.AcquireFrame();
                id=frame?.Identity;
                found=id.HasValue && view.DumpBuffer().Any(row=>row.Contains(marker,StringComparison.Ordinal));
            }
            if(id is { } identity)
            {
                if(acknowledge || !found) session.AcknowledgeFrame(identity.Sequence);
                if(found) return identity;
            }
            if(session.HasExited)
            {
                lock(session.SyncRoot) throw new IOException($"Child exited ({session.ExitCode}) waiting for {marker}: "+
                    string.Join("\n",session.Emulator.DumpBuffer()));
            }
            await Task.Delay(1);
        }
        TerminalFrameIdentity? cached;
        string viewport;
        lock(session.SyncRoot)
        {
            var view=(PresentedTerminalView)session.Emulator;
            using var frame=view.AcquireFrame();
            cached=frame?.Identity;
            viewport=string.Join("\n",view.DumpBuffer());
        }
        string child="unavailable";
        if(session.ChildProcessId is int pid)
        {
            try
            {
                using var progress=System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(
                    @"Local\agwinterm-soak-child-"+pid,System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read);
                using var counters=progress.CreateViewAccessor(0,8,System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                child=$"read={counters.ReadInt32(0)}, flushed={counters.ReadInt32(4)}";
            }
            catch(IOException ex) { child=ex.Message; }
        }
        string owner="no cached identity";
        if(cached is { } last)
        {
            try
            {
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var query=new TerminalQuery(last.Incarnation,last.AttachmentEpoch,last.TerminalGeneration,"search",Text:marker,ResultLimit:2);
                var reply=await session.Presentation!.QueryAsync(query,deadline.Token);
                owner=$"{reply.Status}, generation={reply.Generation}, matches={reply.Matches?.Count}";
            }
            catch(Exception ex) when(ex is OperationCanceledException or IOException or InvalidOperationException) { owner=ex.Message; }
        }
        throw new TimeoutException($"No decoded viewport containing {marker}; child {session.ChildProcessId} {child}; "+
            $"inputClosed={session.InputClosed}, cached={cached}, owner={owner}. Cached viewport:\n{viewport}");
    }
}
