using System.Text;
using System.Text.Json;
using Agwinterm.Core;

namespace Agwinterm.Pty;

public sealed record AgtermMirroredStatus(AgentStatus Status, bool Blink, long ChangedAt, string? Pane);
public sealed record AgtermMirroredNotification(string Title, string Body, string? Pane);

/// <summary>Validated passive replica state. This model never mutates origin-owned local state.</summary>
public sealed class AgtermPassivePresentation
{
    public AgtermMirroredStatus? Status { get; private set; }
    public string? Context { get; private set; }
    public HudSpec? Hud { get; private set; }
    public long? HudDeadline { get; private set; }
    public long HudGeneration { get; private set; }

    public AgtermMirroredNotification? Apply(AgtermPresentationFrame frame, IReadOnlyDictionary<Guid, string> mapping, long tick)
    {
        try
        {
            var body = frame.Body;
            switch (frame.Kind)
            {
                case "snapshot":
                    // Validate the complete replacement before committing any of it.
                    ValidateLayout(Optional(body, "layout"), mapping);
                    var status = ReadStatus(Optional(body, "status"), mapping);
                    var context = ReadContext(Optional(body, "context"));
                    var hud = ReadHud(Optional(body, "hud"), mapping, tick);
                    Status = status; Context = context; (Hud, HudDeadline, HudGeneration) = hud;
                    break;
                case "layout": ValidateLayout(body, mapping); break;
                case "status": Status = ReadStatus(body, mapping); break;
                case "context": Context = ReadContext(body); break;
                case "hud": (Hud, HudDeadline, HudGeneration) = ReadHud(body, mapping, tick); break;
                case "notify":
                    return new(Text(body, "title", 4096, true), Text(body, "body", 16384, true), Pane(body, mapping));
            }
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        { throw new IOException("Unsupported or malformed remote presentation payload", ex); }
    }

    public bool ExpireHud(long tick, long generation)
    {
        if (generation != HudGeneration || HudDeadline is not { } deadline || tick < deadline) return false;
        Hud = null; HudDeadline = null; return true;
    }

    public void Clear() { Status = null; Context = null; Hud = null; HudDeadline = null; HudGeneration = 0; }
    private static JsonElement Optional(JsonElement value, string name) => value.TryGetProperty(name, out var field) ? field : default;
    private static bool Absent(JsonElement value) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
    private static string Text(JsonElement value, string key, int max, bool empty = false)
    {
        var text = value.GetProperty(key).GetString() ?? throw new IOException("Missing presentation text");
        if ((!empty && string.IsNullOrWhiteSpace(text)) || Encoding.UTF8.GetByteCount(text) > max || text.Any(c => char.IsControl(c) && c is not ('\n' or '\t')))
            throw new IOException("Invalid presentation text");
        return text;
    }
    private static string? ReadContext(JsonElement value)
    {
        if (Absent(value)) return null;
        string text = value.GetString()!;
        if (Encoding.UTF8.GetByteCount(text) > 256 || !SessionContexts.TryNormalize(text, out var normalized, out _) || normalized != text)
            throw new IOException("Invalid remote context");
        return text;
    }
    private static string? Pane(JsonElement body, IReadOnlyDictionary<Guid, string> mapping)
    {
        var value = Optional(body, "pane");
        if (Absent(value)) return null;
        if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out var id) || !mapping.TryGetValue(id, out var local))
            throw new IOException("Unknown or unsupported origin pane");
        return local;
    }
    private static AgtermMirroredStatus? ReadStatus(JsonElement body, IReadOnlyDictionary<Guid, string> mapping)
    {
        if (Absent(body)) return null;
        AgentStatus status = Text(body, "status", 32) switch
        { "idle" => AgentStatus.Idle, "active" => AgentStatus.Active, "completed" => AgentStatus.Completed, "blocked" => AgentStatus.Blocked,
            _ => throw new IOException("Unsupported remote status") };
        if (!Absent(Optional(body, "color")) || !Absent(Optional(body, "shape")))
            throw new IOException("Remote status color/shape is not supported by the native status surface");
        double changed = Absent(Optional(body, "changedAt")) ? 0 : body.GetProperty("changedAt").GetDouble();
        if (!double.IsFinite(changed) || changed < 0 || changed > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) throw new IOException("Invalid remote status timestamp");
        return new(status, body.GetProperty("blink").GetBoolean(), (long)changed, Pane(body, mapping));
    }
    private static (HudSpec?, long?, long) ReadHud(JsonElement body, IReadOnlyDictionary<Guid, string> mapping, long tick)
    {
        if (Absent(body)) return (null, null, 0);
        var spec = body.GetProperty("spec");
        var args = new Dictionary<string, object?>();
        foreach (var field in spec.EnumerateObject())
        {
            if (Absent(field.Value)) continue;
            string key = field.Name switch { "backgroundColor" => "color", "textColor" => "text-color", "sizePercent" => "size-percent",
                "hideAfter" => "hide-after", "fontSize" => "font-size", _ => field.Name };
            args.Add(key, field.Value.Clone());
        }
        if (!SessionHuds.TryParse("open", JsonSerializer.SerializeToElement(args), out var hud, out var error)) throw new IOException(error);
        if (!Absent(Optional(spec, "sizePercent")) && spec.GetProperty("sizePercent").GetInt32() != hud!.SizePercent)
            throw new IOException("Remote HUD size cannot be represented without clamping");
        long generation = body.GetProperty("generation").GetInt64();
        if (generation < 0) throw new IOException("Invalid HUD generation");
        double? remaining = Absent(Optional(body, "remaining")) ? null : body.GetProperty("remaining").GetDouble();
        if (remaining is { } seconds && (!double.IsFinite(seconds) || seconds < 0 || seconds > 86400)) throw new IOException("Invalid HUD remaining lifetime");
        if (remaining == 0) return (null, null, generation);
        if (remaining is null && hud!.HideAfter > 0) throw new IOException("Timed HUD omitted remaining lifetime");
        return (hud! with { AnchorPaneId = Pane(body, mapping), HideAfter = remaining ?? 0 },
            remaining is { } lifetime ? checked(tick + (long)Math.Ceiling(lifetime * 1000)) : null, generation);
    }
    private static void ValidateLayout(JsonElement body, IReadOnlyDictionary<Guid, string> mapping)
    {
        if (Absent(body)) return;
        var ids = body.GetProperty("panes").EnumerateArray().Select(p => p.GetGuid()).ToArray();
        if (ids.Length is < 1 or > 2 || ids.Length != mapping.Count || ids.Distinct().Count() != ids.Length || ids.Any(id => !mapping.ContainsKey(id))
            || !ids.Contains(body.GetProperty("primary").GetGuid())) throw new IOException("Origin topology no longer matches imported panes");
        bool shown = body.GetProperty("shown").GetBoolean();
        if (ids.Length == 1 && shown || ids.Length == 2 && Optional(body, "axis").GetString() is not ("vertical" or "horizontal"))
            throw new IOException("Invalid remote pane layout");
    }
}
