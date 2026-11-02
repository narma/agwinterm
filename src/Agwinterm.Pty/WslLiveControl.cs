using System.Text.Json;

namespace Agwinterm.Pty;

public interface IWslLiveHost
{
    object LiveCommand(string command, JsonElement args, string? target);
    JsonElement? LiveTerminalCommand(string command, JsonElement args, ISession session) => null;
}

public static class WslLiveControl
{
    public static void Validate(string command, JsonElement args, string? target)
    {
        string[] fields = command switch
        {
            "zmx.list" => ["profile"], "zmx.attach" => ["profile", "daemon", "incarnation"],
            "zmx.terminate" => ["force"], "zmx.restart" => ["source"],
            "zmx.retry" or "zmx.detach" or "zmx.lead" => [],
            _ => throw new ArgumentException("Unknown zmx operation")
        };
        if (args.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined)) throw new ArgumentException("zmx args must be an object");
        var seen = new HashSet<string>();
        if (args.ValueKind == JsonValueKind.Object)
            foreach (var field in args.EnumerateObject())
            {
                if (!fields.Contains(field.Name) || !seen.Add(field.Name)) throw new ArgumentException("Unknown or duplicate zmx option");
                if (field.Name == "force" ? field.Value.ValueKind != JsonValueKind.True :
                    field.Value.ValueKind != JsonValueKind.String || !WslLiveValidation.Text(field.Value.GetString(), 8192))
                    throw new ArgumentException("Invalid zmx option: " + field.Name);
            }
        if (fields.Any(f => !seen.Contains(f))) throw new ArgumentException("Missing required zmx option");
        if (command is "zmx.list" or "zmx.attach")
        {
            if (target is not null) throw new ArgumentException("This zmx operation selects a profile, not a pane");
        }
        else if (!WslLiveValidation.Text(target) || target == "active") throw new ArgumentException("An exact pane ID is required");
        if (command == "zmx.restart" && args.GetProperty("source").GetString() is not ("shell" or "agent" or "pin" or "captured"))
            throw new ArgumentException("Select shell, agent, pin or captured");
    }
}
