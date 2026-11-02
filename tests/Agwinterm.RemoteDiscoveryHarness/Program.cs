using System.Text.Json;
using Agwinterm.Pty;

// Read-only non-PTY acceptance. This runner never creates UI, terminal clients or workloads.
if (args.Length is not (3 or 4))
    throw new ArgumentException("distribution user SSH-destination [agent-socket] required");
var inventory = await AgtermSshDiscoveryClient.DiscoverAsync(new(args[0], args[1], args[2], args.Length == 4 ? args[3] : null));
Console.WriteLine(JsonSerializer.Serialize(new { inventory.Endpoint, count = inventory.Sessions.Count,
    terminalCandidates = inventory.Sessions.Count(s => s.CanImportTerminal), attachmentVerified = false }));
