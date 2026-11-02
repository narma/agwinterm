using System.Text.Json;

namespace Agwinterm.Pty;

public static class AgtermRemoteControl
{
    public static void Validate(string command, JsonElement args, string? target)
    {
        string[] required = command switch
        {
            "zmx.remote.list" => ["distribution", "user", "host"],
            "zmx.remote.attach" => ["distribution", "user", "host", "bridge", "session"],
            "zmx.remote.retry" or "zmx.remote.detach" or "zmx.remote.lead" => [],
            _ => throw new ArgumentException("Unsupported remote operation; borrowed workloads cannot be restarted or terminated")
        };
        bool discovery = command is "zmx.remote.list" or "zmx.remote.attach";
        var allowed = required.Concat(discovery ? new[] { "agentSocket", "originSocket" } : []).ToHashSet(StringComparer.Ordinal);
        if (args.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined)) throw new ArgumentException("Remote args must be an object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (args.ValueKind == JsonValueKind.Object)
            foreach (var field in args.EnumerateObject())
                if (!allowed.Contains(field.Name) || !seen.Add(field.Name) || field.Value.ValueKind != JsonValueKind.String
                    || !WslLiveValidation.Text(field.Value.GetString(), 4096)) throw new ArgumentException("Invalid or duplicate remote option");
        if (required.Any(f => !seen.Contains(f))) throw new ArgumentException("Missing required remote option");
        if (discovery)
        {
            if (target is not null) throw new ArgumentException("Discovery/import uses origin session selectors, not a local pane");
            Target(args).Validate();
            if (command == "zmx.remote.attach" && (!AgtermRemoteDiscovery.OriginPath(args.GetProperty("bridge").GetString()!)
                || args.GetProperty("session").GetString() == "active")) throw new ArgumentException("Exact origin session and bridge path required");
        }
        else if (!WslLiveValidation.Text(target) || target == "active") throw new ArgumentException("Exact local pane ID required");
    }

    public static AgtermSshTarget Target(JsonElement args) => new(args.GetProperty("distribution").GetString()!,
        args.GetProperty("user").GetString()!, args.GetProperty("host").GetString()!,
        args.TryGetProperty("agentSocket", out var socket) ? socket.GetString() : null,
        args.TryGetProperty("originSocket", out var origin) ? origin.GetString() : null);
}
