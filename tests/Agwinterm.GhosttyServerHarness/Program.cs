using System.Text;
using Agwinterm.Core;
using Agwinterm.Pty;
System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Out));
System.Diagnostics.Trace.AutoFlush=true;

// Run inside the owned Windows VM; no UI, global configuration or user sessions are touched.
if(args is ["--search-load",var searchDirectory,var samplesFile])
{
    await OwnerSearchLoad.Measure(searchDirectory,samplesFile);
    return;
}
if(args is ["--upgrade-child",var pipe,var sessionId])
{
    if(!System.Text.RegularExpressions.Regex.IsMatch(pipe,"^agwinterm-upgrade-test-[0-9a-f]{8}-control-owner-v1$"))
        throw new ArgumentException("An isolated upgrade fixture pipe is required.");
    using var probe=PtyHostClient.Connect(pipe);
    var child=probe.List().SingleOrDefault(s=>s.Id == sessionId);
    Console.WriteLine(child is null ? "absent" : $"{child.ChildPid}:{child.CreationTicket}");
    return;
}
if(args is ["--check-window-history",var stateFile,var paneId,var marker])
{
    if(!RestoreState.TryDeserialize(File.ReadAllText(stateFile),out var state) || state is null)
        throw new InvalidDataException("Closing window has no saved state");
    var pane=state.Workspaces.SelectMany(w=>w.Sessions).SelectMany(s=>s.Panes).Single(p=>p.Id == paneId);
    if(pane.BufferBlob is null || !BufferPersist.TryParse(Convert.FromBase64String(pane.BufferBlob),out var saved) ||
        !saved.Rows.Any(row=>string.Concat(row.Cells.Select(c=>char.ConvertFromUtf32(c.Rune))).Contains(marker,StringComparison.Ordinal)))
        throw new InvalidDataException("Closing window did not persist recent owner history.");
    return;
}
if (args is ["--soak",var appDirectory,var count,var minutes,var report])
{
    await OwnerSoak.Run(appDirectory,int.Parse(count),int.Parse(minutes),report);
    return;
}
if (args.Length != 1) throw new ArgumentException("Pass the application directory (Ghostty and bundled ConPTY).");
Environment.SetEnvironmentVariable("AGWINTERM_GHOSTTY_COUNTERS", "1");
if (!GhosttyVt.TryLoad(args[0], out string? error)) throw new InvalidOperationException(error);
var conpty = ConPtyApi.Select(true, args[0]);
if (!conpty.Bundled) throw new InvalidOperationException(conpty.Description);
TerminalSession.CoreFactory = (cols, rows) => new GhosttyTerminalCore(cols, rows);
string appId = "agwinterm-native-test-" + Guid.NewGuid().ToString("N");
string gateName = @"Local\" + appId;
using var gate = new EventWaitHandle(false, EventResetMode.AutoReset, gateName);
using var ready = new EventWaitHandle(false, EventResetMode.AutoReset, gateName + "-ready");
string script = Path.Combine(Path.GetTempPath(), appId + ".ps1");
int checks = 0;
void Check(bool ok, string name)
{
    if (!ok) throw new InvalidOperationException("FAIL: " + name);
    Console.WriteLine("PASS: " + name); checks++;
}
using (var encoder = new GhosttyTerminalCore(20, 4))
{
    encoder.Feed("\u001b[?9001h"u8);
    Check(encoder.Win32InputMode, "owner input fixture enables Windows physical-key mode");
    var committed = new TerminalInput.Key(0,1,0,"😀é",0x1f600,0,0,0,0,"😀é");
    Check(Encoding.UTF8.GetString(OwnerInputQueue.Encode(encoder,committed)) == "😀é",
        "committed IME text retains native text semantics in Windows input mode");
    var enter = new TerminalInput.Key(13,1,0,"",0,0,13,0,28);
    Check(Encoding.UTF8.GetString(OwnerInputQueue.Encode(encoder,enter)) == "\u001b[13;28;13;1;0;1_",
        "physical Enter still uses owner Windows input mode");
    Check(Encoding.UTF8.GetString(OwnerInputQueue.Encode(encoder,enter with { Action=0 })) == "\u001b[13;28;13;0;0;1_",
        "physical release preserves the owner Windows key-up record");
    encoder.Feed("\u001b[?1000h\u001b[?1006h"u8);
    var mouse=new TerminalInput.Mouse(0,1,0,31,47,9,19,3,2);
    Check(Encoding.UTF8.GetString(OwnerInputQueue.Encode(encoder,mouse)) == "\u001b[<0;4;3M",
        "owner chooses cell mouse coordinates from its current mode");
    encoder.Feed("\u001b[?1016h"u8);
    Check(Encoding.UTF8.GetString(OwnerInputQueue.Encode(encoder,mouse)) == "\u001b[<0;31;47M",
        "same semantic pointer uses pixels after an owner mode change");
}
string Text(ISession session) { lock (session.SyncRoot) return string.Join("\n", session.Emulator.DumpBuffer()); }
using (var local=new TerminalSession(20,4))
{
    var before=GhosttyDiagnostics.Snapshot();
    var presentation=local.EnablePresentation();
    local.Inject("LOCAL é界"u8);
    using var frame=presentation.Capture(0,9,19)!;
    Check(!presentation.IsRemote && local.Presentation == presentation && local.ChildProcessId is null &&
        GhosttyDiagnostics.Snapshot().Owners == before.Owners,
        "local adapter captures directly without another terminal or process");
    var query=new TerminalQuery(frame.Identity.Incarnation,frame.Identity.AttachmentEpoch,frame.Identity.TerminalGeneration,"viewport",0,1);
    var result=await presentation.QueryAsync(query,default);
    Check(result.Status == "ok" && result.Rows.Single().StartsWith("LOCAL é界"),"local viewport query retains a coherent frame generation");
    Check((await presentation.QueryAsync(query with { Incarnation="replaced" },default)).Status == "stale",
        "local query rejects replacement incarnation");
    local.Inject("\u001b_Ga=T,f=32,s=1,v=1,i=77,q=2,C=1;/wAA/w==\u001b\\"u8);
    using(var imageFrame=presentation.Capture(0,8,18)!)
        Check(imageFrame.Images.Count == 1,"owner default pixel geometry makes native images visible");
    using var cancel=new CancellationTokenSource(); cancel.Cancel();
    bool cancelled=false;
    try { await presentation.QueryAsync(query,cancel.Token); }
    catch(OperationCanceledException) { cancelled=true; }
    Check(cancelled,"local query honors cancellation before accessing its frame");
    local.Dispose();
    Check(frame[0,0].Rune == 'L' && GhosttyDiagnostics.Snapshot().Owners == before.Owners-1,
        "local frame lease outlives owner disposal without retaining a native terminal");
}
bool Wait(Func<bool> predicate)
{
    var deadline = DateTime.UtcNow.AddSeconds(15);
    do { if (predicate()) return true; Thread.Sleep(20); } while (DateTime.UtcNow < deadline);
    return predicate();
}
// The child deliberately stops in the middle of CSI. A resize repaint cannot reconstruct this.
File.WriteAllText(script, $$"""
$gate=[Threading.EventWaitHandle]::OpenExisting('{{gateName}}')
$ready=[Threading.EventWaitHandle]::OpenExisting('{{gateName}}-ready')
$esc=[string][char]27
[Console]::Write(($esc+'[?9001l'+("HISTORY`r`n"*40)+$esc+'[?1049h'+$esc+'[2J'+$esc+'[HALT'+$esc+'[?2004h'+$esc+'[31'))
[void]$ready.Set(); [void]$gate.WaitOne()
[Console]::Write(('mRED'+$esc+'[s'+$esc+'[2;1He'+[char]0x0301+[char]0x754C+$esc+']8;;https://example.test/'+$esc+'\LINK'+$esc+']8;;'+$esc+'\'+$esc+'[u'))
[void]$ready.Set(); [void]$gate.WaitOne()
[Console]::Write(($esc+'[0m DETACHED'+$esc+'[?9001h'))
[void]$ready.Set(); [void]$gate.WaitOne()
[Console]::Write(($esc+'[?9001l'+$esc+'[?1049l'))
[void]$ready.Set(); [void]$gate.WaitOne()
exit 7
""");
try
{
    using var host = new PtyHostServer(appId, conpty);
    using var client = PtyHostClient.Connect(appId);
    using var backend = new ServerSessionBackend(appId, exePath: null);
    client.Create("native", 60, 10, "powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script]);
    Check(ready.WaitOne(15000), "child reaches split CSI");
    // Child notification precedes the ConPTY pump. Snapshot retries observe the actual VT state.
    Check(Wait(() =>
    {
        using var attachment = client.Attach("native", nativeSnapshot: true);
        using var core = new GhosttyTerminalCore(1, 1);
        core.RestoreSnapshot(attachment.NativeSnapshot!);
        return core.IsAltScreen && core.DumpRow(0).StartsWith("ALT");
    }), "native snapshot includes alternate screen before first replica");
    Check(client.ThemeRevision == 1 && client.UpdateTheme("native", "112233;445566",
        ThemeColors.FormatPalette(Theme.DefaultPalette()), "778899"), "host accepts a live theme update");
    using (var attachment = client.Attach("native", nativeSnapshot: true))
    using (var themed = new GhosttyTerminalCore(1, 1))
    {
        themed.RestoreSnapshot(attachment.NativeSnapshot!);
        themed.BeginPaint(); var frame = themed.CompletePaint();
        Check(frame[0, 0].Foreground == new Color(0x11, 0x22, 0x33) && themed.PaintCursorColor == new Color(0x77, 0x88, 0x99),
            "detached host snapshot carries new theme without PTY output");
    }
    bool invalidThemeRejected = false;
    try { client.UpdateTheme("native", "invalid", ThemeColors.FormatPalette(Theme.DefaultPalette()), "778899"); }
    catch (InvalidOperationException) { invalidThemeRejected = true; }
    Check(invalidThemeRejected, "host rejects malformed theme without updating terminal state");
    bool rejected = false;
    try { using var legacy = client.Attach("native"); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("native snapshots")) { rejected = true; }
    Check(rejected, "native host rejects a client lacking snapshot support");
    TerminalSession.CoreFactory = null; // UI preference changed; the hosted shell is still Ghostty.
    var first = (ServerSession)backend.Create("native", 60, 10);
    Check(first.Emulator is TerminalEmulator, "replica starts with changed managed UI preference");
    Check(first.TryAdopt(), "first replica adopts the native host");
    Check(first.Emulator is GhosttyTerminalCore && first.Emulator.BracketedPaste, "replica uses Ghostty and restores input modes");
    Check(GhosttyDiagnostics.Snapshot() is { Owners: 1, LegacyReplicas: 1 },
        "real hosted session retains exactly one owner and one legacy replica");
    gate.Set(); Check(ready.WaitOne(15000), "child finishes split CSI");
    Check(Wait(() => Text(first).Contains("ALTRED")), "snapshot continuation joins live output exactly once");
    lock (first.SyncRoot)
        Check(first.Emulator.Screen[0, 3].FgSpec == ColorSpec.Indexed(1), "split CSI preserves red attributes");
    int? pid = first.ChildProcessId;
    first.Detach();
    Check(GhosttyDiagnostics.Snapshot() is { Owners: 1, LegacyReplicas: 0 },
        "detaching releases only the legacy replica");
    Check(Wait(() => !client.List().Single().Attached), "detach closes its data channel without killing the child");
    gate.Set(); Check(ready.WaitOne(15000), "child writes while detached");
    // The legacy oracle belongs to the test harness, not the shipped app package.
    string rustOracle=Path.Combine(AppContext.BaseDirectory,"agwinterm_core.dll");
    if(!File.Exists(rustOracle)) rustOracle=Path.Combine(args[0],"agwinterm_core.dll");
    Check(RustEmulatorCore.TryLoad(rustOracle, out _), "Rust comparison core loads");
    TerminalSession.CoreFactory = (cols, rows) => new RustTerminalCore(cols, rows);
    using var second = (ServerSession)backend.Create("native", 60, 10);
    Check(second.Emulator is RustTerminalCore, "second replica starts with changed Rust UI preference");
    Check(second.TryAdopt(), "second replica adopts the surviving process");
    Check(second.Emulator is GhosttyTerminalCore, "attachment chooses the surviving core instead of the UI preference");
    Check(second.ChildProcessId == pid, "reattach keeps the original child PID");
    Check(Wait(() => Text(second).Contains("ALTRED DETACHED")), "reattach restores detached output without repaint");
    Check(Wait(() => { lock (second.SyncRoot) return second.Emulator.Win32InputMode; }), "reattach restores Windows input mode");
    gate.Set(); Check(ready.WaitOne(15000), "child restores primary screen");
    Check(Wait(() => { lock (second.SyncRoot) return !second.Emulator.IsAltScreen && second.Emulator.HistoryCount > 0; }), "primary screen and history survive alternate-screen reattach");
    second.Resize(72, 12);
    Check(client.List().Single().Cols == 72, "resize reaches the authoritative host");
    gate.Set();
    Check(Wait(() => second.HasExited) && second.ExitCode == 7, "exit follows final output and preserves status");
    Check(client.CoreSelectionRevision == 1, "host advertises per-session core negotiation");
    TerminalSession.CoreFactory = null;
    using (var managed = (ServerSession)backend.Create("managed-choice", 60, 10))
    {
        // Mimic a host whose startup preference is still Ghostty.
        TerminalSession.CoreFactory = (cols, rows) => new GhosttyTerminalCore(cols, rows);
        await managed.StartAsync("cmd.exe", ["/d", "/k"]);
        Check(managed.Emulator is TerminalEmulator && managed.ChildProcessId is not null && !managed.HasExited,
            "new managed session overrides a surviving Ghostty host default");
    }
    using (var native = (ServerSession)backend.Create("ghostty-choice", 60, 10))
    {
        TerminalSession.CoreFactory = null; // inverse case: surviving host has a managed default.
        await native.StartAsync("cmd.exe", ["/d", "/k"]);
        Check(native.Emulator is GhosttyTerminalCore && native.ChildProcessId is not null && !native.HasExited,
            "new Ghostty session overrides a surviving managed host default");
    }
    TerminalSession.CoreFactory = (cols, rows) => new RustTerminalCore(cols, rows);
    var rustFirst = (ServerSession)backend.Create("rust-reconnect", 60, 10);
    await rustFirst.StartAsync("cmd.exe", ["/d", "/q", "/k", "echo RUST_RECONNECT_SCREEN"]);
    bool rustVisible = Wait(() => Text(rustFirst).Contains("RUST_RECONNECT_SCREEN"));
    if (!rustVisible) Console.WriteLine($"Rust diagnostic: exited={rustFirst.HasExited}; pid={rustFirst.ChildProcessId}; text={Text(rustFirst)}");
    Check(rustVisible, "Rust host receives visible output");
    int? rustPid = rustFirst.ChildProcessId;
    rustFirst.Detach();
    Check(Wait(() => !client.List().Single(s => s.Id == "rust-reconnect").Attached), "Rust replica detaches without closing its shell");
    TerminalSession.CoreFactory = null;
    using (var rustSecond = (ServerSession)backend.Create("rust-reconnect", 60, 10))
    {
        Check(rustSecond.TryAdopt() && rustSecond.Emulator is RustTerminalCore, "Rust reattachment adopts the authoritative core");
        Check(rustSecond.ChildProcessId == rustPid && Wait(() => Text(rustSecond).Contains("RUST_RECONNECT_SCREEN")),
            "bundled ConPTY Rust reattachment restores the visible screen without child redraw");
    }
    using (var presentationBackend = new ServerSessionBackend(appId,exePath:null,replicaCore:"ghostty",singleOwner:true))
    {
        var countsBefore=GhosttyDiagnostics.Snapshot();
        var single=(ServerSession)presentationBackend.Create("single-owner",60,10);
        var effects=new HarnessEffects(); single.Emulator.Host=effects;
        Check(single.Emulator is PresentedTerminalView && GhosttyDiagnostics.Snapshot() == countsBefore,
            "presentation handle creates no UI terminal");
        await single.StartAsync("powershell.exe",["-NoProfile","-ExecutionPolicy","Bypass","-File",script]);
        Check(ready.WaitOne(15000),"single owner child reaches split CSI");
        Check(Wait(()=>Present(single).Contains("ALT")),"first presentation is a current viewport without VT replay");
        var live=GhosttyDiagnostics.Snapshot();
        Check(live.Owners == countsBefore.Owners+1 && live.LegacyReplicas == countsBefore.LegacyReplicas,
            "hosted presentation adds exactly one owner and zero replicas");
        bool oldAttachRejected=false;
        try { using var old=client.Attach("single-owner",nativeSnapshot:true); }
        catch(InvalidOperationException ex) when(ex.Message.Contains("presentation-capable")) { oldAttachRejected=true; }
        Check(oldAttachRejected,"single owner never silently downgrades to raw VT transport");
        gate.Set(); Check(ready.WaitOne(15000),"single owner finishes split CSI");
        Check(Wait(()=>Present(single).Contains("ALTRED")),"presentation preserves split escape state in its sole owner");
        Check(Wait(()=>Present(single).Contains("LINK")),"presentation receives grapheme and hyperlink fixture");
        // ConPTY may consume Kitty APCs before the terminal owner sees them.
        // The display-injection contract exercises the exact owner/frame/bulk path.
        single.Inject("\u001b[s\u001b[31m\u001b[2;1Hé界\u001b]8;;https://example.test/\u001b\\LINK\u001b]8;;\u001b\\\u001b[4;1H\u001b_Ga=T,f=32,s=1,v=1,i=77,q=2,C=1;/wAA/w==\u001b\\\u001b[u"u8);
        bool imageReady=Wait(()=>
        {
            Present(single);
            lock(single.SyncRoot)
            {
                using var frame=((PresentedTerminalView)single.Emulator).AcquireFrame();
                return frame?.Images.Any(p=>p.Id == 77 && p.Image.Pixels.Span.SequenceEqual(new byte[] {255,0,0,255})) == true;
            }
        });
        if(!imageReady)
        {
            lock(single.SyncRoot)
            {
                using var frame=((PresentedTerminalView)single.Emulator).AcquireFrame();
                Console.WriteLine($"Image diagnostic: closed={single.InputClosed}; frame={frame?.Identity}; images={frame?.Images.Count}");
                if(frame is not null) foreach(var p in frame.Images)
                    Console.WriteLine($"Image {p.Id}: {p.Image.Format} {p.Image.Width}x{p.Image.Height}; bytes={Convert.ToHexString(p.Image.Pixels.Span)}");
            }
        }
        Check(imageReady,"presentation resolves image pixels over bounded bulk queries without a UI terminal");
        lock(single.SyncRoot)
        {
            using var frame=((PresentedTerminalView)single.Emulator).AcquireFrame()!;
            Check(frame[1,0].Grapheme == "é" && frame[1,0].Width == 1,
                "single-owner viewport preserves combining grapheme");
            Check(frame[1,1].Rune == 0x754c && frame[1,1].Width == 2 && frame[1,2].Width == 0,
                "single-owner viewport preserves wide cell and continuation");
            Check(frame[1,3].Hyperlink?.Uri == "https://example.test/" && frame[1,3].FgSpec == ColorSpec.Indexed(1),
                "single-owner viewport preserves link and indexed style");
            Check(frame.State.CursorRow == 0 && frame.State.CursorColumn == 6,
                "single-owner viewport preserves restored cursor metadata");
        }
        int? singlePid=single.ChildProcessId;
        (ulong sequence,ulong revision,ulong generation) Animation(bool acknowledge)
        {
            lock(single.SyncRoot)
            {
                using var f=((PresentedTerminalView)single.Emulator).AcquireFrame();
                if(f is null) return default;
                if(acknowledge) single.AcknowledgeFrame(f.Identity.Sequence);
                return (f.Identity.Sequence,f.Identity.ViewportRevision,
                    f.Images.FirstOrDefault(p=>p.Id == 97)?.Image.Generation ?? 0);
            }
        }
        single.Inject(Encoding.ASCII.GetBytes("\u001b7\u001b_Ga=T,f=32,i=97,s=1,v=1,q=2,C=1;/wAA/w==\u001b\\"+
            "\u001b_Ga=f,f=32,i=97,s=1,v=1,z=40,q=2;AAD//w==\u001b\\"+
            "\u001b_Ga=a,i=97,r=1,z=30,q=2\u001b\\\u001b_Ga=a,i=97,s=3,q=2\u001b\\\u001b8"));
        Check(Wait(()=>Animation(true).generation != 0),"animated image reaches the hosted viewport");
        var initialAnimation=Animation(true);
        Check(Wait(()=>Animation(true).generation != initialAnimation.generation),"owner advances animation without child output");
        // Let one more frame arrive but withhold its credit. No later frame
        // may supersede that unacknowledged viewport.
        var beforeStall=Animation(true);
        Check(Wait(()=>Animation(false).sequence > beforeStall.sequence),"animated viewport awaits presentation credit");
        var stalled=Animation(false);
        Thread.Sleep(160);
        Check(Animation(false).sequence == stalled.sequence,"stalled reader retains just one animated viewport");
        single.Presentation!.Execute(new TerminalCommand.Viewport(0,false,stalled.revision+1));
        single.AcknowledgeFrame(stalled.sequence);
        Thread.Sleep(120);
        var hidden=Animation(false);
        Thread.Sleep(120);
        Check(Animation(false).sequence == hidden.sequence,"hidden animation does not continuously publish");
        single.Inject("\u001b]2;HIDDEN_OWNER_TITLE\u0007\u001b]7;file://localhost/tmp/owner-cwd\u0007"u8);
        Check(Wait(()=>{lock(single.SyncRoot) return single.Emulator.Title == "HIDDEN_OWNER_TITLE" && single.Emulator.Cwd.EndsWith("/owner-cwd");}),
            "hidden pane receives durable title and cwd without a viewport credit");
        single.Presentation.Execute(new TerminalCommand.Viewport(0,true,stalled.revision+2));
        Check(Wait(()=>Animation(true).revision >= stalled.revision+2),"visible animation resumes with a fresh viewport");
        single.Inject("\u001b_Ga=a,i=97,s=1,q=2\u001b\\\u001b7\u001b[8;1HANIMATION_STOPPED\u001b8"u8);
        Check(Wait(()=>Present(single).Contains("ANIMATION_STOPPED")),"animation stop reached the owner");
        var stopped=Animation(true);
        long quietSince=Environment.TickCount64;
        Check(Wait(()=>{
            var current=Animation(true);
            if(current.sequence != stopped.sequence){stopped=current;quietSince=Environment.TickCount64;}
            return Environment.TickCount64-quietSince >= 400;
        }),"stopped hosted animation needs no periodic publication");
        string pngPath=Path.Combine(Path.GetTempPath(),appId+".png");
        try
        {
            File.WriteAllBytes(pngPath,Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=="));
            using var control=new ControlServer(single);
            string imageCommand=System.Text.Json.JsonSerializer.Serialize(new { cmd="image.frame",
                args=new { images=new[] { new { path=pngPath,id=88,row=3,col=0,cols=2,rows=2 } } } });
            using(var response=System.Text.Json.JsonDocument.Parse(control.Dispatch(imageCommand)))
                Check(response.RootElement.GetProperty("ok").GetBoolean(),"image.frame control commits in the owner process");
            Check(Wait(()=>
            {
                Present(single);
                lock(single.SyncRoot) { using var frame=((PresentedTerminalView)single.Emulator).AcquireFrame();
                    return frame?.Images.Any(p=>p.Id == 88 && p.Image.Format == GhosttyPixelFormat.Png &&
                        p.Image.Width == 1 && p.Image.Height == 1 && p.Image.Pixels.Span.SequenceEqual(File.ReadAllBytes(pngPath))) == true; }
            }),"owner image.frame publishes immutable PNG payload and placements");
            using(var response=System.Text.Json.JsonDocument.Parse(control.Dispatch("{\"cmd\":\"image.clear\",\"args\":{}}")))
                Check(response.RootElement.GetProperty("ok").GetBoolean(),"image.clear control reaches the owner");
            Check(Wait(()=>{ Present(single); lock(single.SyncRoot) { using var frame=((PresentedTerminalView)single.Emulator).AcquireFrame();
                return frame is not null && frame.Images.Count == 0; } }),"owner image clear removes transported placements");
        }
        finally { File.Delete(pngPath); }
        single.Inject("\u001b]52;c;Y2xpcGJvYXJk\a\a\u001b]9;4;2;25\a"u8);
        Check(Wait(()=>effects.Events.Contains("clipboard:clipboard") && effects.Events.Contains("bell") && effects.Events.Contains("progress:2:25")),
            "owner forwards clipboard bell and progress on its ordered reliable channel");
        string ticket=single.CreationTicket;
        single.Detach();
        gate.Set(); Check(ready.WaitOne(15000),"single owner receives detached output");
        using var reattached=(ServerSession)presentationBackend.Create("single-owner",60,10);
        var restoredEffects=new HarnessEffects(); reattached.Emulator.Host=restoredEffects;
        Check(reattached.TryAdopt(ticket) && reattached.ChildProcessId == singlePid,"presentation reattach retains exact shell incarnation");
        Check(Wait(()=>{lock(reattached.SyncRoot) return reattached.Emulator.Title == "HIDDEN_OWNER_TITLE" && reattached.Emulator.Cwd.EndsWith("/owner-cwd");}),
            "new attachment receives the current title and cwd");
        Check(Wait(()=>Present(reattached).Contains("ALTRED DETACHED")),"reattach sends latest viewport without a history replica");
        Check(Wait(()=>restoredEffects.Events.Contains("progress:2:25")) && !restoredEffects.Events.Contains("bell") &&
            !restoredEffects.Events.Any(e=>e.StartsWith("clipboard:")),"reattach restores durable progress without replaying ephemeral effects");
        gate.Set(); Check(ready.WaitOne(15000),"presentation child restores primary screen");
        Check(Wait(()=>{ Present(reattached); lock(reattached.SyncRoot) return !reattached.Emulator.IsAltScreen && reattached.Emulator.HistoryCount > 0; }),
            "presentation carries primary screen and history metadata");
        using(var oldViewport=reattached.Presentation!.Capture(5,11,23)) { }
        Check(Wait(()=>
        {
            Present(reattached);
            lock(reattached.SyncRoot)
            {
                using var frame=((PresentedTerminalView)reattached.Emulator).AcquireFrame();
                return frame is not null && frame.Identity.ViewportRevision > 0 && frame.State.ScrollOffset == 5;
            }
        }),"semantic viewport update publishes scroll state in a new generation");
        ulong beforeResize;
        lock(reattached.SyncRoot)
        {
            using var frame=((PresentedTerminalView)reattached.Emulator).AcquireFrame()!;
            beforeResize=frame.Identity.ViewportRevision;
        }
        reattached.Resize(72,12);
        Check(Wait(()=>
        {
            Present(reattached);
            lock(reattached.SyncRoot)
            {
                using var frame=((PresentedTerminalView)reattached.Emulator).AcquireFrame();
                return frame is not null && frame.State.Columns == 72 && frame.State.Rows == 12 && frame.Identity.ViewportRevision > beforeResize;
            }
        }),
            "resize publishes a complete new viewport");
        TerminalQuery ownerQuery;
        lock(reattached.SyncRoot)
        {
            using var frame=((PresentedTerminalView)reattached.Emulator).AcquireFrame()!;
            ownerQuery=new(frame.Identity.Incarnation,frame.Identity.AttachmentEpoch,frame.Identity.TerminalGeneration,"history",0,16);
        }
        var history=await reattached.Presentation!.QueryAsync(ownerQuery,default);
        Check(history.Status == "ok" && history.Rows.Any(row=>row.Contains("HISTORY")),
            "bounded owner history query reads retained history without a UI replica");
        Check(SurfaceText.Dump(reattached,new OverlayTextArgs(All:true,Lines:0)).Contains("HISTORY"),
            "control text reads owner history across bounded coherent pages");
        using(var styled=System.Text.Json.JsonDocument.Parse(SurfaceText.DumpStyled(reattached,new OverlayTextArgs(All:true,Lines:0))))
            Check(styled.RootElement.GetProperty("rows").GetArrayLength() > 12,
                "styled control readback includes owner history beyond the cached viewport");
        var found=await reattached.Presentation.QueryAsync(ownerQuery with { Operation="search",Text="HISTORY",ResultLimit=3 },default);
        Check(found.Status == "ok" && found.Matches?.Count == 3 && found.Truncated,
            "owner search returns bounded native matches independently of frame credits");
        Check((await reattached.Presentation.QueryAsync(ownerQuery with { Generation=0 },default)).Status == "stale",
            "owner history query refuses a mixed generation");
        Check((await reattached.Presentation.QueryAsync(ownerQuery with { RowCount=33 },default)).Status == "invalid",
            "owner query rejects oversized history pages");
        using(var cancelledQuery=new CancellationTokenSource())
        {
            cancelledQuery.Cancel(); bool refused=false;
            try { await reattached.Presentation.QueryAsync(ownerQuery,cancelledQuery.Token); }
            catch(OperationCanceledException) { refused=true; }
            Check(refused,"owner query cancellation leaves input and presentation attached");
        }
        int selectedRow=history.Rows.Select((text,index)=>(text,index)).First(pair=>pair.text.Contains("HISTORY")).index;
        int selectedColumn=history.Rows[selectedRow].IndexOf("HISTORY",StringComparison.Ordinal);
        var selectionRequest=new TerminalSelectionRequest(Guid.NewGuid().ToString("N"),selectedRow,selectedColumn,
            selectedRow,selectedColumn+6,false);
        string selectionJson=System.Text.Json.JsonSerializer.Serialize(selectionRequest);
        var selectionReply=await reattached.Presentation.QueryAsync(ownerQuery with { Operation="selection-create",Text=selectionJson },default);
        var selection=selectionReply.Rows.Count == 1 ? System.Text.Json.JsonSerializer.Deserialize<TerminalSelectionResult>(selectionReply.Rows[0]) : null;
        Check(selectionReply.Status == "ok" && selection is { Valid:true,Text:"HISTORY" },
            "selection owner captures immutable copy text and native tracked endpoints");
        reattached.Resize(80,12);
        Check(Wait(()=>{ Present(reattached); lock(reattached.SyncRoot) return reattached.Emulator.Screen.Cols == 80; }),
            "selection fixture applies resize before locating anchors");
        var locatedReply=await reattached.Presentation.QueryAsync(ownerQuery with { Operation="selection-locate",Text=selectionJson },default);
        var located=locatedReply.Rows.Count == 1 ? System.Text.Json.JsonSerializer.Deserialize<TerminalSelectionResult>(locatedReply.Rows[0]) : null;
        Check(locatedReply.Status == "ok" && located is { Valid:true } && locatedReply.Generation > ownerQuery.Generation,
            "owner selection survives resize and reports its new generation");
        await reattached.Presentation.QueryAsync(ownerQuery with { Operation="selection-release",Text=selectionJson },default);
        Check((await reattached.Presentation.QueryAsync(ownerQuery with { Operation="selection-locate",Text=selectionJson },default)).Status == "stale",
            "released selection cannot reuse native anchor handles");
        using(var liveViewport=reattached.Presentation.Capture(0,11,23)) { }
        reattached.Inject("\r\nOWNER_DISPLAY_INJECTION"u8);
        Check(Wait(()=>Present(reattached).Contains("OWNER_DISPLAY_INJECTION")),
            "display injection mutates the sole host terminal without sending child input");
        var all=selectionRequest with { Id=Guid.NewGuid().ToString("N"),All=true };
        var allReply=await reattached.Presentation.QueryAsync(ownerQuery with { Operation="selection-create",
            Text=System.Text.Json.JsonSerializer.Serialize(all) },default);
        var allText=allReply.Rows.Count == 1 ? System.Text.Json.JsonSerializer.Deserialize<TerminalSelectionResult>(allReply.Rows[0])?.Text : null;
        Check(allReply.Generation > ownerQuery.Generation && allText?.Contains("HISTORY") == true && allText.Contains("OWNER_DISPLAY_INJECTION"),
            "select-all uses one coherent current buffer after output advanced past the painted frame");
        byte[] checkpoint=await reattached.ExportBufferAsync();
        Check(BufferPersist.TryParse(checkpoint,out var savedBuffer) && savedBuffer.Rows.Count > 10,
            "host exports attributed history beyond the UI viewport");
        string liveBefore=Present(reattached);
        int historyBefore=reattached.Emulator.HistoryCount;
        await reattached.ImportBufferAsync(BufferPersist.FromPlainText(["LEGACY_STATE_IMPORT\u001b[31m"],60));
        Check(Wait(()=>{ Present(reattached); return reattached.Emulator.HistoryCount > historyBefore; }),
            "old plain state restores through the owner without a UI terminal");
        Check(Present(reattached) == liveBefore,"history import preserves the fresh shell screen");
        Check(SurfaceText.Dump(reattached,new OverlayTextArgs(All:true,Lines:0)).Contains("LEGACY_STATE_IMPORT [31m"),
            "legacy saved escape sequences remain inert history text");
        reattached.Inject("\r\nHELD_EXIT_CREDIT"u8);
        Check(Wait(()=>{
            lock(reattached.SyncRoot)
            {
                if(reattached.Emulator.DumpBuffer().Any(row=>row.Contains("HELD_EXIT_CREDIT"))) return true;
                using var frame=((PresentedTerminalView)reattached.Emulator).AcquireFrame();
                if(frame is not null) reattached.AcknowledgeFrame(frame.Identity.Sequence);
                return false;
            }
        }),
            "exit fixture holds the last visible frame without acknowledging it");
        gate.Set();
        Check(Wait(()=>{
            if(!reattached.HasExited) reattached.SubmitInput(new TerminalInput.Raw(" "u8.ToArray()));
            return reattached.HasExited;
        }) && reattached.ExitCode == 7,
            "occluded presentation closes input and receives its final viewport without a new paint");
        Check(SurfaceText.Dump(reattached,new OverlayTextArgs(All:true,Lines:0)).Contains("LEGACY_STATE_IMPORT"),
            "exited session keeps owner history readable");
        Check(reattached.SubmitInput(new TerminalInput.Raw("never replay"u8.ToArray())) == TerminalInputAcceptance.Closed,
            "exited session rejects child input");
        reattached.Resize(70,10);
        Check(Wait(()=>{ Present(reattached); return reattached.Emulator.Screen.Cols == 70; }),
            "exited terminal still reflows through its owner");
        reattached.Inject("\r\nPRESS_ENTER_TO_CLOSE"u8);
        Check(Wait(()=>Present(reattached).Contains("PRESS_ENTER_TO_CLOSE")),
            "exit prompt is injected at the owner after child exit");
        var dense=new StringBuilder("\r\n");
        for(int row=0;row<40;row++)
        {
            for(int col=0;col<50;col++) dense.Append(col%2 == 0 ? "\u001b[31m§" : "\u001b[32m§");
            dense.Append("\u001b[0m\r\n");
        }
        dense.Append("DENSE_END");
        reattached.Inject(Encoding.UTF8.GetBytes(dense.ToString()));
        Check(Wait(()=>Present(reattached).Contains("DENSE_END")),"dense history fixture completed");
        using var styles=System.Text.Json.JsonDocument.Parse(SurfaceText.DumpStyled(reattached,new(true,0)));
        int styledCells=styles.RootElement.GetProperty("rows").EnumerateArray()
            .SelectMany(row=>row.GetProperty("runs").EnumerateArray())
            .Count(run=>run.GetProperty("text").GetString() == "§" && run.GetProperty("fg").GetString() is "idx:1" or "idx:2");
        Check(styledCells == 2000,"colored history pages preserve every alternating style beyond the default JSON budget");
    }
    Console.WriteLine($"{checks} server checks passed.");
}
finally { File.Delete(script); TerminalSession.CoreFactory = null; }
Check(Wait(() => GhosttyDiagnostics.Snapshot() is { Owners: 0, LegacyReplicas: 0, ActiveCaptures: 0, SearchHandles: 0 }),
    "host and client disposal release all counted native resources");

static string Present(ServerSession session)
{
    string text; ulong sequence;
    lock(session.SyncRoot)
    {
        var view=(PresentedTerminalView)session.Emulator;
        using var frame=view.AcquireFrame();
        sequence=frame?.Identity.Sequence ?? 0;
        text=string.Join("\n",view.DumpBuffer());
    }
    if(sequence != 0) session.AcknowledgeFrame(sequence);
    return text;
}
