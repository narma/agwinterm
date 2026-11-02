using System.Text.Json;
using Agwinterm.Pty;

if (args.Length != 8) throw new ArgumentException("distro user host agent origin-socket bridge fixture host-exe required");
var target = new AgtermSshTarget(args[0], args[1], args[2], args[3], args[4]);
var inventory = await AgtermSshDiscoveryClient.DiscoverAsync(target);
var origin = inventory.Sessions.First(s => s.CanImportTerminal).Session;
var operations = new AgtermRemoteOperations();
var binding = await operations.ProbeAsync(target, args[5], inventory.Endpoint, origin, origin.Panes[0]);
string id = "aw-remote-terminal-" + Guid.NewGuid().ToString("N");
using var backend = new ServerSessionBackend(id, args[7], "--pipe " + id, "server-rust");
ServerSession? first = null, second = null;
try
{
    // The supplied script belongs to the isolated origin fixture; never address a user's session.
    if (!args[6].StartsWith("/tmp/aw-stage2-source-", StringComparison.Ordinal) || args[6].Any(c => !char.IsAsciiLetterOrDigit(c) && !"/._-".Contains(c)))
        throw new ArgumentException("isolated test fixture path required");
    await operations.TypeAsync(binding, "python3 " + args[6] + " " + id + "\n");
    first = (ServerSession)backend.Create("first", 80, 24);
    await Attach(first, "first");
    await Wait(() => { lock (first.SyncRoot) return Ready(first); });
    AssertCells(first);
    lock (first.SyncRoot)
        if (!first.Emulator.BracketedPaste || first.Emulator.KeyboardFlags != 1) throw new Exception("input modes missing");
    var screen = await operations.ScreenAsync(binding, false);
    second = (ServerSession)backend.Create("second", 110, 36);
    await Attach(second, "second");
    await Wait(() => { lock (second.SyncRoot) return Ready(second); });
    AssertCells(second);
    var followerScreen = await operations.ScreenAsync(binding, false);
    if (screen.GetProperty("columns").GetInt32() != followerScreen.GetProperty("columns").GetInt32()
        || screen.GetProperty("rows").GetInt32() != followerScreen.GetProperty("rows").GetInt32()) throw new Exception("follower stole geometry");
    await operations.TypeAsync(binding, "q");
    await Wait(() => operations.ScreenAsync(binding, false).GetAwaiter().GetResult().GetProperty("text").GetString()!.Contains("QUERY-REPLIES-1"));
    first.Dispose(); first = null;
    second.Dispose(); second = (ServerSession)backend.Create("reconnected", 80, 24);
    await Attach(second, "reconnected");
    await Wait(() => { lock (second.SyncRoot) return Ready(second); });
    AssertCells(second);
    await operations.TypeAsync(binding, "n");
    await Wait(() => { lock (second.SyncRoot) return !second.Emulator.IsAltScreen && second.Emulator.DumpBuffer().Any(l => l.Contains("NORMAL-RESTORED")); });
    string history;
    lock (second.SyncRoot) history = string.Join('\n', second.Emulator.DumpBuffer());
    for (int i = 0; i < 90; i++)
        if (history.Split("HISTORY-" + id + "-" + i.ToString("D3")).Length != 2) throw new Exception("lost/duplicated hidden primary history " + i + "\n" + history);
    await operations.RevalidateAsync(binding);
    Console.WriteLine("PASS real agterm + Rust viewers: styled wide cells, alternate/modes, follower geometry, one query reply, hidden primary history once, exact reconnect");
}
finally
{
    // Stop only the explicitly started fixture program, keeping the borrowed shell alive.
    try { await operations.TypeAsync(binding, "x"); } catch { }
    first?.Dispose(); second?.Dispose();
    using var host = PtyHostClient.Connect(id);
    System.Diagnostics.Process.GetProcessById((int)host.HostPid).Kill();
}
async Task Attach(ServerSession session, string nonce)
{
    var command = AgtermRemoteOperations.AttachmentArgs(binding, nonce);
    await session.StartAsync("wsl.exe", new SessionCommand("wsl.exe", command).QuotedArgs, verbatimCommandLine: true);
}
static async Task Wait(Func<bool> predicate)
{
    for (int i = 0; i < 100; i++) { if (predicate()) return; await Task.Delay(100); }
    throw new Exception("terminal state did not converge");
}
static void AssertCells(ServerSession session)
{
    lock (session.SyncRoot)
    {
        var cells = session.Emulator.Screen;
        if (cells[0,6].Rune != 0x754c || cells[0,6].Width != 2 || cells[0,7].Width != 0
            || cells[0,9].Rune != 0x1f600 || cells[0,9].Width != 2 || cells[0,0].Foreground != new Agwinterm.Core.Color(12,34,56))
            throw new Exception("styled Unicode snapshot corrupted");
    }
}

static bool Ready(ServerSession session)
{
    try
    {
        AssertCells(session);
        return session.Emulator.IsAltScreen && session.Emulator.BracketedPaste && session.Emulator.KeyboardFlags == 1;
    }
    catch { return false; }
}
