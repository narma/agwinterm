using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agwinterm.Core;

namespace Agwinterm.Pty;

public sealed record HudSpec(string Message, string? Detail, string Spinner, string? BackgroundColor,
    string? TextColor, int? SizePercent, string Position, double HideAfter = 0, bool Sticky = false,
    bool Frame = true, double? FontSize = null, bool Markdown = false, [property: JsonIgnore] string? Pane = null)
{
    public string? AnchorPaneId { get; init; }
    public double EffectiveFontSize { get; init; } = FontSize ?? 13;
    public bool Visible { get; init; }
    public HudGeometry? Geometry { get; init; }
    [JsonIgnore] public HudDocument? Document { get; init; }
}
public sealed record HudGeometry(int X, int Y, int Width, int Height);

/// <summary>Host-independent HUD validation, read-back and bounded nine-anchor geometry.</summary>
public static class SessionHuds
{
    public const int MaxTextLength = 256;
    public static readonly string[] Positions = ["top-left", "top-center", "top-right", "center-left",
        "center", "center-right", "bottom-left", "bottom-center", "bottom-right"];
    public static readonly string[] Spinners = ["none", "bar", "braille", "circle", "blocks", "dot"];
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static bool TryParse(string action, JsonElement args, out HudSpec? spec, out string? error)
    {
        spec = null; error = null;
        if (action is not ("open" or "update" or "close")) { error = "hud: expected open, update or close"; return false; }
        if (args.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
        { error = "hud: args must be an object"; return false; }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        double hideAfter = 0; double? fontSize = null; bool sticky = false, frame = true, markdown = false;
        int? width = null;
        if (args.ValueKind == JsonValueKind.Object)
        foreach (var field in args.EnumerateObject())
        {
            if (!seen.Add(field.Name)) { error = "hud: duplicate option '" + field.Name + "'"; return false; }
            if (action == "close") { error = "hud close takes no display options"; return false; }
            if (field.Name is "hide-after" or "font-size")
            {
                if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetDouble(out double n) || !double.IsFinite(n) ||
                    (field.Name == "hide-after" ? n is < 0 or > 86400 : n is < 6 or > 72))
                { error = "hud: hide-after must be 0..86400 seconds and font-size 6..72 DIP"; return false; }
                if (field.Name == "hide-after") hideAfter = n; else fontSize = n;
                continue;
            }
            if (field.Name is "sticky" or "frame" or "markdown")
            {
                if (field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { error = "hud: " + field.Name + " must be boolean"; return false; }
                if (field.Name == "sticky") sticky = field.Value.GetBoolean();
                else if (field.Name == "frame") frame = field.Value.GetBoolean(); else markdown = field.Value.GetBoolean();
                continue;
            }
            if (field.Name == "size-percent")
            {
                if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out int n) || n is < 1 or > 100)
                { error = "hud: size-percent must be a whole number in 1..100"; return false; }
                width = n; continue;
            }
            if (field.Name is not ("message" or "detail" or "spinner" or "color" or "text-color" or "position" or "pane"))
            { error = "hud: unknown option '" + field.Name + "'"; return false; }
            if (field.Value.ValueKind != JsonValueKind.String)
            { error = "hud: '" + field.Name + "' must be text"; return false; }
            values[field.Name] = field.Value.GetString()!;
        }
        if (action == "close") return true;
        string Read(string key, string fallback = "") => values.GetValueOrDefault(key, fallback);
        string message = Read("message"), detail = Read("detail");
        if (string.IsNullOrWhiteSpace(message)) { error = "hud requires a nonblank message"; return false; }
        foreach (var (value, formatted) in new[] { (message, markdown), (detail, false) })
        {
            if (value.Any(c => char.IsControl(c) && !(formatted && c is '\r' or '\n' or '\t'))) { error = "hud text must not contain control characters"; return false; }
            // Normalize validates UTF-16 too; malformed Unicode is a refusal, never a render exception.
            try
            {
                int limit = formatted ? HudMarkdown.MaxScalars : MaxTextLength;
                if (value.Normalize().EnumerateRunes().Count() > limit)
                { error = $"hud text exceeds {limit} Unicode scalars"; return false; }
            }
            catch (ArgumentException) { error = "hud text is not valid Unicode"; return false; }
        }
        string position = Read("position", "center") switch { "top" => "top-center", "bottom" => "bottom-center", var p => p };
        if (!Positions.Contains(position)) { error = "hud: invalid position; use " + string.Join('|', Positions) + " (top/bottom aliases accepted)"; return false; }
        if (width is { } requested) width = Math.Clamp(requested, 10, sticky && position != "center" ? 100 : 80);
        string? pane = values.GetValueOrDefault("pane");
        if (pane is not null && (string.IsNullOrWhiteSpace(pane) || pane.Length > 256 || pane.Any(char.IsControl)))
        { error = "hud: invalid pane selector"; return false; }
        string spinner = Read("spinner", "none");
        if (!Spinners.Contains(spinner)) { error = "hud: invalid spinner; use " + string.Join('|', Spinners); return false; }
        foreach (string key in new[] { "color", "text-color" })
        {
            if (!values.TryGetValue(key, out string? color)) continue;
            string digits = color.StartsWith('#') ? color[1..] : color;
            if (digits.Length != 6 || !digits.All(char.IsAsciiHexDigit))
            { error = "hud: " + key + " must be #rrggbb"; return false; }
            values[key] = "#" + digits.ToLowerInvariant();
        }
        HudDocument? document = null;
        try { if (markdown) document = HudMarkdown.Parse(message); }
        catch (ArgumentException ex) { error = ex.Message; return false; }
        spec = new(message.Normalize(), string.IsNullOrEmpty(detail) ? null : detail.Normalize(), spinner,
            values.GetValueOrDefault("color"), values.GetValueOrDefault("text-color"), width, position, hideAfter, sticky, frame, fontSize, markdown, pane)
            { Document = document };
        return true;
    }

    public static string Reply(string session, HudSpec? hud) => JsonSerializer.Serialize(new { session, hud }, JsonOptions);

    public static string? ResolvePane(string selector, IReadOnlyList<string> paneIds, int focused)
    {
        if (paneIds.Contains(selector, StringComparer.Ordinal)) return selector;
        int index = selector switch { "left" => 0, "right" => 1, "focused" => focused, _ => -1 };
        return index >= 0 && index < paneIds.Count ? paneIds[index] : null;
    }

    public static long? Deadline(long nowMilliseconds, double seconds) => seconds == 0 ? null : checked(nowMilliseconds + (long)Math.Ceiling(seconds * 1000));

    public sealed record Target(string Id, string Name, IEnumerable<string> SurfaceIds);

    /// <summary>HUD-only session routing, shared with the contract fake. Exact session IDs win;
    /// other surface IDs never widen, and a prefix must identify one owning session's own ID.</summary>
    public static string? ResolveTarget(string target, IEnumerable<Target> candidates, IEnumerable<string> standaloneIds)
    {
        if (string.IsNullOrEmpty(target)) return null;
        var sessions = candidates.ToArray();
        var standalone = standaloneIds.ToArray();
        if (sessions.Any(s => s.Id == target)) return target;
        if (sessions.Any(s => s.SurfaceIds.Contains(target)) || standalone.Contains(target)) return null;
        var prefixes = sessions.Where(s => s.Id.StartsWith(target, StringComparison.Ordinal) ||
            s.SurfaceIds.Any(id => id.StartsWith(target, StringComparison.Ordinal))).ToArray();
        if (standalone.Any(id => id.StartsWith(target, StringComparison.Ordinal))) return null;
        if (prefixes.Length != 0)
            return prefixes.Length == 1 && prefixes[0].Id.StartsWith(target, StringComparison.Ordinal) ? prefixes[0].Id : null;
        var named = sessions.Where(s => string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase)).ToArray();
        return named.Length == 1 ? named[0].Id : null;
    }
    public readonly record struct Box(float X, float Y, float Width, float Height);
    public static Box Place(float x, float y, float width, float height, float neededWidth, float neededHeight, HudSpec spec)
    {
        width = Math.Max(0, width); height = Math.Max(0, height);
        float cap = spec.Sticky && spec.Position != "center" ? 1 : .8f;
        float w = spec.SizePercent is { } n ? width * n / 100 : Math.Clamp(neededWidth, width * .1f, width * cap);
        float h = Math.Clamp(neededHeight, 0, height * .8f);
        int i = Array.IndexOf(Positions, spec.Position); if (i < 0) i = 4;
        float margin = spec.Sticky && spec.Position != "center" ? 0 : .1f;
        float Offset(float extent, float size, int band) => band switch
        { 0 => extent * margin, 2 => extent * (1 - margin) - size, _ => (extent - size) / 2 };
        return new(x + Offset(width, w, i % 3), y + Offset(height, h, i / 3), w, h);
    }

    public static string Frame(string spinner, long milliseconds)
    {
        string[] frames = spinner switch {
            "bar" => ["|", "/", "-", "\\"], "braille" => ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"],
            "circle" => ["◐", "◓", "◑", "◒"], "blocks" => ["▁", "▃", "▄", "▅", "▆", "▇", "▆", "▅", "▄", "▃"],
            "dot" => ["●", " "], _ => [""] };
        int interval = spinner switch { "braille" or "blocks" => 80, "circle" => 120, "dot" => 450, _ => 100 };
        return frames[(int)((Math.Max(0, milliseconds) / interval) % frames.Length)];
    }
}
