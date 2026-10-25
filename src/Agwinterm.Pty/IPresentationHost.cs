using System.Text.Json;
using Agwinterm.Core;

namespace Agwinterm.Pty;

public interface IPresentationHost
{
    object PresentationCommand(string command, JsonElement args, string? target);
}

public static class PresentationRequest
{
    public static bool Applies(string command) => command is "session.appearance" or "keymap.list" or "config.reload" or "app.version";
    public static string? String(JsonElement args, string key) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value)
        ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw new ArgumentException("expected text: " + key) : null;
    public static PaneAppearance? Validate(string command, JsonElement args, string? target)
    {
        if (target is not null && (command != "session.appearance" || string.IsNullOrWhiteSpace(target))) throw new ArgumentException("unsupported or empty target");
        if (args.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null)) throw new ArgumentException("args must be an object");
        string op = String(args, "op") ?? "get";
        if (command == "session.appearance" && op is not ("get" or "set" or "clear")) throw new ArgumentException("appearance expects get/set/clear");
        var allowed = command == "session.appearance" ? op == "set" ? new[] { "op", "color", "text", "opacity", "status-color", "status-shape", "hide-image" } : ["op"] : Array.Empty<string>();
        int opacity = 15; bool hide = false;
        if (args.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>();
            foreach (var p in args.EnumerateObject())
            {
                if (!seen.Add(p.Name) || !allowed.Contains(p.Name)) throw new ArgumentException("unknown or duplicate presentation argument: " + p.Name);
                if (p.Name == "opacity") { if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out opacity)) throw new ArgumentException("opacity must be an integer"); }
                else if (p.Name == "hide-image") { if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("hide-image must be boolean"); hide = p.Value.GetBoolean(); }
                else if (p.Value.ValueKind != JsonValueKind.String) throw new ArgumentException("expected text: " + p.Name);
            }
        }
        return command == "session.appearance" && op == "set" ? new PaneAppearance(String(args,"color"), String(args,"text"), opacity, String(args,"status-color"), String(args,"status-shape"), hide).Validate() : null;
    }
}
