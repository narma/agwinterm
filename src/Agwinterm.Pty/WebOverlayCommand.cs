using System.Text.Json;

namespace Agwinterm.Pty;

public sealed record WebOverlayCommand(string Action, string? Direction, string? PageId, bool Current = false, string? Pane = null)
{
    public static bool Applies(string action, JsonElement args) => action is "reload" or "navigate" ||
        action == "result" && args.ValueKind == JsonValueKind.Object && args.TryGetProperty("page", out _);
    public static WebOverlayCommand Parse(string action, JsonElement args)
    {
        var names = new HashSet<string>();
        foreach (var p in args.EnumerateObject())
        {
            if (!names.Add(p.Name)) throw new ArgumentException("duplicate web operation argument: " + p.Name);
            if (action == "reload" && p.Name == "current")
            {
                if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("current must be a boolean");
                continue;
            }
            if (p.Name != "action" && !(action is "reload" or "navigate" && p.Name == "pane") && !(action == "navigate" && p.Name == "direction") && !(action == "result" && p.Name == "page"))
                throw new ArgumentException("unsupported web operation argument: " + p.Name);
            if (p.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(p.Value.GetString()))
                throw new ArgumentException(p.Name + " must be a nonempty string");
        }
        string? direction = args.TryGetProperty("direction", out var d) ? d.GetString() : null;
        string? page = args.TryGetProperty("page", out var p2) ? p2.GetString() : null;
        if (action == "navigate" && direction is not ("back" or "forward" or "browser" or "finder")) throw new ArgumentException("navigate requires back, forward, browser or finder");
        if (action == "result" && page is null) throw new ArgumentException("page result requires an exact page ID");
        string? pane = args.TryGetProperty("pane", out var selector) ? selector.GetString() : null;
        if (!OverlayPanes.TryParse(pane, out _, out var error)) throw new ArgumentException(error);
        return new(action, direction, page, args.TryGetProperty("current", out var current) && current.GetBoolean(), pane);
    }
}
