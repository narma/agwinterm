using System.Diagnostics;
using System.Globalization;
using System.Text;
using Agwinterm.Core;
using Agwinterm.Pty;

internal static class OwnerSearchLoad
{
    public static async Task Measure(string directory,string output)
    {
        string id="search-load-"+Guid.NewGuid().ToString("N");
        using var backend=(ServerSessionBackend)SessionBackends.Resolve("server",id,Path.Combine(directory,"Agwinterm.Win32.exe"),"bundled");
        try
        {
            using var session=(ServerSession)backend.Create("search",80,24);
            var presentation=session.Presentation!;
            presentation.Execute(new TerminalCommand.ScrollbackLimit(50000));
            string runtime=Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? throw new InvalidOperationException("Guest runtime missing");
            await session.StartAsync(Path.Combine(runtime,"dotnet.exe"),[Path.Combine(directory,"echo-fixture","Agwinterm.EchoFixture.dll"),"soak"],freshEnv:false);
            var frame=await OwnerSoak.WaitFor(session,"SOAK_READY");
            session.Inject("HOLD_FRAME_CREDIT\r\n"u8);
            var held=await OwnerSoak.WaitFor(session,"HOLD_FRAME_CREDIT",acknowledge:false);
            const int flood=256;
            var enter=new TerminalInput.Key(13,1,0,"",0,0,13,0,28);
            for(int i=0;i<flood;i++)
                if(session.SubmitInput(enter) != TerminalInputAcceptance.Accepted)
                    throw new IOException("Input was refused while the frame reader was stalled");
            string finalMarker=$"SOAK_{flood:D8}!";
            using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                var query=new TerminalQuery(held.Incarnation,held.AttachmentEpoch,held.TerminalGeneration,"search",Text:finalMarker,ResultLimit:1);
                while(true)
                {
                    var reply=await presentation.QueryAsync(query,deadline.Token);
                    if(reply.Status == "ok" && reply.Matches?.Count == 1) break;
                    await Task.Delay(10,deadline.Token);
                }
            }
            lock(session.SyncRoot)
            {
                using var cached=((PresentedTerminalView)session.Emulator).AcquireFrame();
                if(cached?.Identity != held) throw new IOException("The publisher exceeded its single outstanding credit");
            }
            session.Resize(79,24);
            session.AcknowledgeFrame(held.Sequence);
            frame=await OwnerSoak.WaitFor(session,finalMarker);
            if(frame.ViewportRevision <= held.ViewportRevision) throw new IOException("Stalled viewport did not resynchronize after resize");
            Console.WriteLine("PASS 256 inputs and 1 MiB of echo output while one viewport credit was held; resized client resumed at the final marker");
            // Each marker fences a batch, keeping the same bounded production
            // input queue used by an interactive client.
            for(int batch=0;batch<80;batch++)
            {
                var rows=new StringBuilder();
                for(int row=0;row<500;row++)
                    rows.Append($"SEARCH_HISTORY {batch:D2} {row:D3} large history fixture\r\n");
                string marker=$"BATCH_DONE_{batch:D2}!";
                rows.Append(marker).Append("\r\n");
                session.Inject(Encoding.UTF8.GetBytes(rows.ToString()));
                frame=await OwnerSoak.WaitFor(session,marker);
            }
            lock(session.SyncRoot)
                if(session.Emulator.HistoryCount<39000) throw new InvalidDataException("Large history was not retained");
            using var samples=new StreamWriter(output);
            samples.WriteLine("block,sequence,search,searchPendingAtSubmit,searchStatus,echoMs,searchMs");
            int sequence=flood,overlaps=0;
            for(int block=0;block<10;block++)
            {
                // Alternate order across successive pairs.
                bool search=block%4 is 1 or 2;
                for(int sample=0;sample<50;sample++)
                {
                    long start=Stopwatch.GetTimestamp();
                    var operation=search ? Query() : null;
                    bool pending=operation is { IsCompleted:false };
                    if(pending) overlaps++;
                    if(session.SubmitInput(new TerminalInput.Key(13,1,0,"",0,0,13,0,28)) != TerminalInputAcceptance.Accepted)
                        throw new IOException("Echo input was refused while searching");
                    frame=await OwnerSoak.WaitFor(session,$"SOAK_{++sequence:D8}!");
                    double echoMs=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    var result=operation is null ? ("off",0d) : await operation;
                    samples.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"{block},{sequence},{search},{pending},{result.Item1},{echoMs:F4},{result.Item2:F4}"));

                    async Task<(string,double)> Query()
                    {
                        var query=new TerminalQuery(frame.Incarnation,frame.AttachmentEpoch,frame.TerminalGeneration,
                            "search",Text:"NO_MATCH_IN_THE_SEEDED_HISTORY",ResultLimit:8);
                        var reply=await presentation.QueryAsync(query,default);
                        if(reply.Status is not ("ok" or "stale")) throw new IOException("Search failed: "+reply.Status);
                        return (reply.Status,Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    }
                }
            }
            if(overlaps==0) throw new InvalidOperationException("Search did not overlap any input submission");
            Console.WriteLine($"PASS {sequence-flood} sequential echoes over 40,000+ rows; {overlaps} submissions with an outstanding search");
        }
        finally
        {
            if(PtyHostClient.IsRunning(id+"-owner-v1"))
            {
                using var admin=PtyHostClient.Connect(id+"-owner-v1");
                admin.Shutdown();
            }
        }
    }
}
