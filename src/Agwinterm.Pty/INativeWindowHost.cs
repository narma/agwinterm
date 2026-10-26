using System.Text.Json;

namespace Agwinterm.Pty;

public interface INativeWindowHost
{
    object NativeWindowCommand(string command, JsonElement args, string? target, string? window);
}

public static class NativeWindowRequest
{
    public static bool Applies(string command, JsonElement args) => command is
        "window.next" or "window.previous" or "window.minimize" or "window.restore" or "window.fullscreen" or "window.monitors" or "window.geometry" or "session.reveal" ||
        command == "window.new" && Has(args, "minimized") || command == "window.move" && Has(args, "monitor");
    private static bool Has(JsonElement args, string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out _);
    public static string? String(JsonElement args, string key) => Has(args, key) ? args.GetProperty(key).GetString() : null;
    public static bool Bool(JsonElement args, string key) => Has(args, key) && args.GetProperty(key).GetBoolean();
    public static void Validate(string command, JsonElement args, string? target, string? window)
    {
        if (target is not null && string.IsNullOrWhiteSpace(target) || window is not null && string.IsNullOrWhiteSpace(window)) throw new ArgumentException("empty selector");
        if (command != "session.reveal" && window is not null) throw new ArgumentException("window operations use --target, not --window");
        if (command is "window.new" or "window.monitors" && target is not null) throw new ArgumentException("this operation does not accept a target");
        string[] allowed = command switch { "window.new" => ["name", "minimized"], "window.move" => ["monitor"], "window.fullscreen" => ["op"], "session.reveal" => ["path-only"], _ => [] };
        if (args.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("args must be an object");
            var seen = new HashSet<string>();
            foreach (var p in args.EnumerateObject())
            {
                if (!seen.Add(p.Name) || !allowed.Contains(p.Name)) throw new ArgumentException("unknown or duplicate argument: " + p.Name);
                if (p.Name is "minimized" or "path-only") { if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("expected boolean: " + p.Name); }
                else if (p.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(p.Value.GetString()) || p.Value.GetString()!.Length > 4096) throw new ArgumentException("invalid text: " + p.Name);
            }
        }
        if (command == "window.fullscreen" && (String(args, "op") ?? "toggle") is not ("on" or "off" or "toggle")) throw new ArgumentException("fullscreen expects on/off/toggle");
        if (command == "window.move" && String(args, "monitor") is null) throw new ArgumentException("monitor required");
    }
}
