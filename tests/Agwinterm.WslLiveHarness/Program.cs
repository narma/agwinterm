using System.Text.Json;
using Agwinterm.Pty;

if (args.Length != 4) throw new ArgumentException("distribution Linux-executable Rust-host-executable terminal-fixture required");
string id = "aw-live-" + Guid.NewGuid().ToString("N");
var profile = new ShellProfile { Name = "live-test", Command = "wsl.exe", Cwd = "/tmp",
    Args = ["-d", args[0], "--exec", "python3", "-c",
        "from pathlib import Path; import time; Path('/tmp/" + id + "').open('x').write('1'); print('BRIDGE-SENTINEL', flush=True); time.sleep(300)"],
    Zmx = new WslZmxConfiguration(true, args[1]) };
var operations = new WslLiveOperations();
WslLiveBinding? binding = null;
ServerSession? first = null, second = null;
using var backend = new ServerSessionBackend(id, args[2], "--pipe " + id, "server-rust");
try
{
    binding = await operations.CreateAsync(profile, id, "pane", "/tmp", new Dictionary<string, string>(), saved =>
    {
        binding = saved;
        if (!saved.Pending || !saved.IsValid) throw new Exception("invalid reservation");
        return Task.CompletedTask;
    });
    if (binding.Recovery != "fresh-started" || binding.Identity is null) throw new Exception("create did not become ready");
    first = (ServerSession)backend.Create("pane", 80, 24);
    string[] argv = WslLiveOperations.AttachmentArgs(binding, "first");
    await first.StartAsync("wsl.exe", new SessionCommand("wsl.exe", argv).QuotedArgs, verbatimCommandLine: true);
    if (first.HasExited || string.IsNullOrEmpty(first.CreationTicket)) throw new Exception("Rust transport failed");
    binding = binding with { TransportTicket = first.CreationTicket };
    var incarnation = binding.Identity;
    int? clientPid = first.ChildProcessId;
    first.Detach();
    second = (ServerSession)backend.Create("pane", 80, 24);
    if (second.TryAdopt("wrong-ticket")) throw new Exception("mismatched transport adopted");
    if (!second.TryAdopt(binding.TransportTicket) || second.ChildProcessId != clientPid)
        throw new Exception("matching transport not adopted");
    Console.WriteLine("PASS exact Rust ticket adopts the same Windows WSL client");
    second.Dispose();
    second = null;
    binding = await operations.ReconcileAsync(binding);
    if (binding.Identity != incarnation || binding.Recovery != "zmx-reattached") throw new Exception("workload changed");
    second = (ServerSession)backend.Create("replacement-pane", 100, 30);
    argv = WslLiveOperations.AttachmentArgs(binding, "reattached");
    await second.StartAsync("wsl.exe", new SessionCommand("wsl.exe", argv).QuotedArgs, verbatimCommandLine: true);
    if (second.HasExited) throw new Exception("reattachment failed");
    Console.WriteLine("PASS fresh Rust transport reattaches the same Linux incarnation " + JsonSerializer.Serialize(incarnation));
    if (await operations.TerminateAsync(binding) != "terminated") throw new Exception("termination unconfirmed");
    binding = await operations.ReconcileAsync(binding);
    if (binding.Recovery != "missing") throw new Exception("loss not reported");
    Console.WriteLine("PASS exact termination and missing recovery without replay");
    var beforeRestart = binding;
    WslLiveBinding? interrupted = null;
    try
    {
        await operations.RestartAsync(binding, "pin", "echo MUST_NOT_RUN", pending =>
        {
            interrupted = JsonSerializer.Deserialize<WslLiveBinding>(JsonSerializer.Serialize(pending));
            throw new IOException("Simulated exit after checkpoint, before Linux reservation");
        });
        throw new Exception("interrupted restart unexpectedly returned");
    }
    catch (IOException) { }
    if (interrupted is not { IsValid: true, RestartOrigin: not null }) throw new Exception("restart origin was not durable");
    binding = await operations.ReconcileAsync(interrupted);
    if (!binding.IsValid || binding.BindingId != beforeRestart.BindingId || binding.Identity != beforeRestart.Identity
        || binding.Recovery != "missing" || binding.RestartOrigin is not null)
        throw new Exception("interrupted restart did not restore exact predecessor");
    var restarted = await operations.RestartAsync(binding, "pin", "sleep 300", _ => Task.CompletedTask);
    if (restarted.Recovery != "fresh-started" || !restarted.IsValid) throw new Exception("restart after rollback failed");
    await operations.TerminateAsync(restarted);
    // A lost success response followed by workload exit still has to publish its
    // exact new identity, not retain an identity-less, invalid missing binding.
    var lostReply = restarted with { Identity = null, Pending = true, Recovery = "uncertain",
        RestartOrigin = new(binding.BindingId, binding.Identity!) };
    binding = await operations.ReconcileAsync(lostReply);
    if (!binding.IsValid || binding.Identity != restarted.Identity || binding.Recovery != "missing")
        throw new Exception("ended restart lost its identity during reconciliation");
    Console.WriteLine("PASS durable restart rollback, subsequent explicit restart, and ended lost-reply recovery");
    second.Dispose(); second = null;
    var cycleClock = System.Diagnostics.Stopwatch.StartNew();
    using (var inventory = PtyHostClient.Connect(id))
    {
        int baselineTransports = inventory.List().Count;
        for (int cycle = 0; cycle < 3; cycle++)
        {
            string pane = "cycle-" + cycle;
            profile.Args = ["-d", args[0], "--exec", "python3", "-c", "import time; time.sleep(300)"];
            binding = await operations.CreateAsync(profile, id, pane, "/tmp", new Dictionary<string, string>(), _ => Task.CompletedTask);
            var exact = binding.Identity;
            first = (ServerSession)backend.Create(pane, 80, 24);
            await Attach(first, binding, pane, false);
            string cycleTicket = first.CreationTicket!;
            first.Detach();
            second = (ServerSession)backend.Create(pane, 80, 24);
            if (!second.TryAdopt(cycleTicket)) throw new Exception("cycle adoption failed");
            second.Dispose(); second = null;
            binding = await operations.ReconcileAsync(binding);
            if (binding.Identity != exact) throw new Exception("cycle duplicated workload");
            second = (ServerSession)backend.Create(pane + "-new", 80, 24);
            await Attach(second, binding, pane + "-new", false);
            second.Dispose(); second = null;
            if (await operations.TerminateAsync(binding) != "terminated") throw new Exception("cycle termination failed");
            await Wait(() => inventory.List().Count == baselineTransports);
        }
        Console.WriteLine("PASS three create/adopt/reattach/detach/terminate cycles; no retained Windows transports; elapsedMs=" + cycleClock.ElapsedMilliseconds);
    }
    profile.Args = ["-d", args[0], "--exec", "python3", "-c", File.ReadAllText(args[3])];
    binding = await operations.CreateAsync(profile, id, "terminal-pane", "/tmp", new Dictionary<string, string>(), _ => Task.CompletedTask);
    first = (ServerSession)backend.Create("terminal-pane", 80, 24);
    await Attach(first, binding, "terminal-first", true);
    try { await Wait(() => { lock (first.SyncRoot) return first.Emulator.IsAltScreen && first.Emulator.DumpRow(0).Contains("COLOR-"); }); }
    catch { lock (first.SyncRoot) Console.WriteLine("INITIAL TERMINAL alt=" + first.Emulator.IsAltScreen + " exited=" + first.HasExited + "\n" + string.Join('\n', first.Emulator.DumpBuffer())); throw; }
    string baseline = Snapshot(first);
    AssertCells(first);
    string ticket = first.CreationTicket!;
    first.Detach();
    second = (ServerSession)backend.Create("terminal-pane", 80, 24);
    if (!second.TryAdopt(ticket)) throw new Exception("terminal adoption refused");
    try { await Wait(() => Snapshot(second) == baseline); }
    catch
    {
        using var expected = JsonDocument.Parse(baseline);
        using var actual = JsonDocument.Parse(Snapshot(second));
        foreach (var property in expected.RootElement.EnumerateObject())
            if (property.Value.GetRawText() != actual.RootElement.GetProperty(property.Name).GetRawText())
                Console.WriteLine("ADOPTION DIFFERENCE " + property.Name + ": expected " + property.Value.GetRawText()[..Math.Min(500, property.Value.GetRawText().Length)]
                    + " actual " + actual.RootElement.GetProperty(property.Name).GetRawText()[..Math.Min(500, actual.RootElement.GetProperty(property.Name).GetRawText().Length)]);
        throw;
    }
    Console.WriteLine("PASS Rust adoption preserves alternate screen, Unicode/wide cells, colors, cursor and input modes");
    await operations.TypeAsync(binding, "n");
    await Wait(() => { lock (second.SyncRoot) return !second.Emulator.IsAltScreen && second.Emulator.DumpBuffer().Any(l => l.Contains("NORMAL-RESTORED")); });
    AssertHistory(second, "Rust adoption");
    await operations.TypeAsync(binding, "a");
    await Wait(() => { lock (second.SyncRoot) return second.Emulator.IsAltScreen; });
    first = (ServerSession)backend.Create("terminal-follower", 60, 18);
    await Attach(first, binding, "terminal-follower", false);
    await Wait(() => { lock (first.SyncRoot) return first.Emulator.DumpRow(0).Contains("COLOR-"); });
    using (var orphanClient = PtyHostClient.Connect(id))
    {
        var orphanArgs = WslLiveOperations.AttachmentArgs(binding, "unclaimed-test");
        orphanClient.Create("unclaimed-test", 80, 24, "wsl.exe", new SessionCommand("wsl.exe", orphanArgs).QuotedArgs, verbatim: true);
        backend.ReapUnclaimedStartupSessions();
        await Wait(() => orphanClient.List().All(s => s.Id != "unclaimed-test"));
        if ((await operations.ReconcileAsync(binding)).Identity != binding.Identity) throw new Exception("orphan cleanup changed workload");
        Console.WriteLine("PASS Windows startup orphan cleanup removes only the unclaimed attachment");
    }
    await operations.TypeAsync(binding, "q");
    await Wait(() => operations.ScreenAsync(binding, false).GetAwaiter().GetResult().GetProperty("text").GetString()!.Contains("QUERY-REPLIES-1"));
    second.Dispose(); second = null;
    first.Dispose(); first = null;
    second = (ServerSession)backend.Create("terminal-reconnected", 80, 24);
    await Attach(second, binding, "terminal-reconnected", false);
    await Wait(() => { lock (second.SyncRoot) return second.Emulator.IsAltScreen && second.Emulator.DumpRow(0).Contains("COLOR-"); });
    lock (second.SyncRoot)
    {
        if (!second.Emulator.BracketedPaste || second.Emulator.KeyboardFlags != 1) throw new Exception("input modes lost on daemon reconnect");
    }
    AssertCells(second);
    await operations.TypeAsync(binding, "n");
    await Wait(() => { lock (second.SyncRoot) return !second.Emulator.IsAltScreen && second.Emulator.DumpBuffer().Any(l => l.Contains("NORMAL-RESTORED")); });
    AssertHistory(second, "daemon reconnect");
    var history = (await operations.ScreenAsync(binding, true)).GetProperty("text").GetString()!;
    for (int i = 0; i < 90; i++)
        if (history.Split("HISTORY-" + i.ToString("D3")).Length != 2) throw new Exception("lost/duplicated authoritative history line " + i);
    Console.WriteLine("PASS daemon reconnect preserves modes and normal history; two clients emit one terminal query reply");
}

finally
{
    first?.Dispose();
    second?.Dispose();
    if (binding?.Identity is not null) await operations.TerminateAsync(binding);
    using var host = PtyHostClient.Connect(id);
    using var hostProcess = System.Diagnostics.Process.GetProcessById((int)host.HostPid);
    Console.WriteLine("RESOURCE Rust host workingSetBytes=" + hostProcess.WorkingSet64 + " peakWorkingSetBytes=" + hostProcess.PeakWorkingSet64);
    hostProcess.Kill();
}

async Task Attach(ServerSession session, WslLiveBinding current, string nonce, bool claim)
{
    var command = WslLiveOperations.AttachmentArgs(current, nonce, claim);
    await session.StartAsync("wsl.exe", new SessionCommand("wsl.exe", command).QuotedArgs, verbatimCommandLine: true);
}
static async Task Wait(Func<bool> condition)
{
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (DateTime.UtcNow < deadline) { if (condition()) return; await Task.Delay(50); }
    throw new Exception("terminal state did not converge");
}
static string Snapshot(ServerSession session)
{
    lock (session.SyncRoot)
    {
        var e = session.Emulator;
        return JsonSerializer.Serialize(new { e.IsAltScreen, e.CursorCol, e.CursorRow, e.BracketedPaste, e.KeyboardFlags,
            rows = Enumerable.Range(0, 6).Select(row => Enumerable.Range(0, 30).Select(col => e.Screen[row, col]).ToArray()).ToArray() });
    }
}

static void AssertHistory(ServerSession session, string path)
{
    string text;
    lock (session.SyncRoot) text = string.Join('\n', session.Emulator.DumpBuffer());
    for (int i = 0; i < 90; i++)
        if (text.Split("HISTORY-" + i.ToString("D3")).Length != 2)
            throw new Exception(path + " lost/duplicated visible history line " + i + ": " + text);
}

static void AssertCells(ServerSession session)
{
    lock (session.SyncRoot)
    {
        var cells = session.Emulator.Screen;
        if (cells[0, 6].Rune != 0x754c || cells[0, 6].Width != 2 || cells[0, 7].Width != 0 ||
            cells[0, 9].Rune != 0x1f600 || cells[0, 9].Width != 2 || cells[0, 0].Foreground != new Agwinterm.Core.Color(12, 34, 56))
            throw new Exception("styled Unicode/wide-cell snapshot was corrupted");
    }
}
