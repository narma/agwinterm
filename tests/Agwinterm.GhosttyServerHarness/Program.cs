using System.Text;
using Agwinterm.Core;
using Agwinterm.Pty;

// Run inside the owned Windows VM; no UI, global configuration or user sessions are touched.
if (args.Length != 1) throw new ArgumentException("Pass the application directory (Ghostty and bundled ConPTY).");
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
string Text(ISession session) { lock (session.SyncRoot) return string.Join("\n", session.Emulator.DumpBuffer()); }
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
[Console]::Write('mRED')
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
    bool rejected = false;
    try { using var legacy = client.Attach("native"); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("native snapshots")) { rejected = true; }
    Check(rejected, "native host rejects a client lacking snapshot support");
    TerminalSession.CoreFactory = null; // UI preference changed; the hosted shell is still Ghostty.
    var first = (ServerSession)backend.Create("native", 60, 10);
    Check(first.Emulator is TerminalEmulator, "replica starts with changed managed UI preference");
    Check(first.TryAdopt(), "first replica adopts the native host");
    Check(first.Emulator is GhosttyTerminalCore && first.Emulator.BracketedPaste, "replica uses Ghostty and restores input modes");
    gate.Set(); Check(ready.WaitOne(15000), "child finishes split CSI");
    Check(Wait(() => Text(first).Contains("ALTRED")), "snapshot continuation joins live output exactly once");
    lock (first.SyncRoot)
        Check(first.Emulator.Screen[0, 3].FgSpec == ColorSpec.Indexed(1), "split CSI preserves red attributes");
    int? pid = first.ChildProcessId;
    first.Detach();
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
    Console.WriteLine($"{checks} server checks passed.");
}
finally { File.Delete(script); TerminalSession.CoreFactory = null; }
