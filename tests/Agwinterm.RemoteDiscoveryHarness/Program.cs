using System.Text.Json;
using Agwinterm.Pty;

// Read-only non-PTY acceptance. This runner never creates UI, terminal clients or workloads.
if (args.Length == 7 && args[0] == "--presentation")
{
    var target = new AgtermSshTarget(args[1], args[2], args[3], args[4], args[5]);
    var discovered = await AgtermSshDiscoveryClient.DiscoverAsync(target);
    var session = discovered.Sessions.First(s => s.CanImportTerminal).Session;
    var binding = await new AgtermRemoteOperations().ProbeAsync(target, args[6], discovered.Endpoint,
        session, session.Panes[0], presentationVersion: discovered.PresentationVersion);
    await new AgtermRemoteOperations().RevalidateAsync(binding);
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    bool snapshot = false;
    try
    {
        await new AgtermPresentationTransport().RunAsync(binding, ["status", "context", "hud", "layout", "notify"],
            (frame, _) => { snapshot |= frame.Kind == "snapshot"; if (snapshot) stop.Cancel(); return Task.CompletedTask; }, stop.Token);
    }
    catch (OperationCanceledException) when (snapshot) { }
    if (!snapshot) throw new IOException("No real-peer snapshot received");
    Console.WriteLine(JsonSerializer.Serialize(new { presentationSnapshot = true, terminalCreated = false }));
    return;
}
if (args.Length is not (3 or 4))
    throw new ArgumentException("distribution user SSH-destination [agent-socket] required");
var inventory = await AgtermSshDiscoveryClient.DiscoverAsync(new(args[0], args[1], args[2], args.Length == 4 ? args[3] : null));
Console.WriteLine(JsonSerializer.Serialize(new { inventory.Endpoint, count = inventory.Sessions.Count,
    terminalCandidates = inventory.Sessions.Count(s => s.CanImportTerminal), attachmentVerified = false }));
