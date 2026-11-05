using System.Text;
using Agwinterm.Core;
using Agwinterm.Pty;

// Run inside the owned Windows VM; no UI, global configuration or user sessions are touched.
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
    Check(Encoding.UTF8.GetString(OwnerInputQueue.Encode(encoder,mouse)) == "\u001b[<0;32;48M",
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
    Check(RustEmulatorCore.TryLoad(Path.Combine(args[0], "agwinterm_core.dll"), out _), "Rust comparison core loads");
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
        string ticket=single.CreationTicket;
        single.Detach();
        gate.Set(); Check(ready.WaitOne(15000),"single owner receives detached output");
        using var reattached=(ServerSession)presentationBackend.Create("single-owner",60,10);
        Check(reattached.TryAdopt(ticket) && reattached.ChildProcessId == singlePid,"presentation reattach retains exact shell incarnation");
        Check(Wait(()=>Present(reattached).Contains("ALTRED DETACHED")),"reattach sends latest viewport without a history replica");
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
        gate.Set();
        Check(Wait(()=>{ Present(reattached); return reattached.HasExited; }) && reattached.ExitCode == 7,
            "presentation exposes exit only after its final viewport");
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
