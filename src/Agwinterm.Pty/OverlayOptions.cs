using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>Additive options; absence remains distinguishable from a sizing reset.</summary>
public sealed record OverlayOptions(int? Percent = null, int? Width = null, int? Height = null,
    bool Full = false, string? Cwd = null, bool Follow = false, string? BackgroundColor = null)
{
    public bool Extended => Width is not null || Height is not null || Full || Cwd is not null || Follow || BackgroundColor is not null;
    public bool HasSize => Percent is not null || Width is not null || Height is not null || Full;

    public static OverlayOptions Parse(string action, JsonElement args)
    {
        if (action is not ("open" or "close" or "result" or "copy" or "text" or "resize" or "hide" or "show" or "toggle"))
            throw new ArgumentException("unknown overlay action: " + action);
        if (args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new();
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("overlay args must be an object");
        if (args.TryGetProperty("action", out var actionValue) && actionValue.ValueKind != JsonValueKind.String)
            throw new ArgumentException("overlay action must be a string");
        if (args.TryGetProperty("command", out var commandValue) && commandValue.ValueKind != JsonValueKind.String)
            throw new ArgumentException("overlay command must be a string");
        var seen = new HashSet<string>();
        foreach (var p in args.EnumerateObject())
        {
            if (!seen.Add(p.Name)) throw new ArgumentException("duplicate overlay argument: " + p.Name);
            bool allowed = p.Name is "action" or "pane" || action switch
            {
                "open" => p.Name is "command" or "size-percent" or "width" or "height" or "cwd" or "follow" or "background-color" or "wait" or "block",
                "resize" => p.Name is "size-percent" or "width" or "height" or "full",
                "text" => p.Name is "all" or "lines" or "styles",
                _ => false
            };
            if (!allowed) throw new ArgumentException("overlay " + action + ": unsupported argument " + p.Name);
        }
        int? Integer(string key, int max)
        {
            if (!args.TryGetProperty(key, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int n) || n < 1 || n > max)
                throw new ArgumentException(key + " must be a whole number in 1.." + max);
            return n;
        }
        bool Boolean(string key)
        {
            if (!args.TryGetProperty(key, out var v)) return false;
            if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException(key + " must be boolean");
            return v.GetBoolean();
        }
        string? Text(string key)
        {
            if (!args.TryGetProperty(key, out var v)) return null;
            if (v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString())) throw new ArgumentException(key + " must be a nonempty string");
            return v.GetString();
        }
        var options = new OverlayOptions(Integer("size-percent",100), Integer("width",16384), Integer("height",16384), Boolean("full"), Text("cwd"), Boolean("follow"), Text("background-color"));
        if (options.Full && (options.Percent is not null || options.Width is not null || options.Height is not null)) throw new ArgumentException("--full conflicts with other overlay sizes");
        if (options.BackgroundColor is { } c && (c.Length != 7 || c[0] != '#' || c.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF")))
            throw new ArgumentException("background-color must be #RRGGBB");
        if (args.TryGetProperty("pane", out _) && (options.Width is not null || options.Height is not null || options.Full))
            throw new ArgumentException("pane overlays are always full-pane; floating dimensions are unsupported");
        return options;
    }
}
