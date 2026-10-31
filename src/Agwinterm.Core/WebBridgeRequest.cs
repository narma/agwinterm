using System.Text;
using System.Text.Json;

namespace Agwinterm.Core;

/// <summary>Trusted local-page control envelope. Remote documents never reach this parser.</summary>
public sealed record WebBridgeRequest(string Id, string Method, string? Target, string? Window, JsonElement Args)
{
    public static bool TargetsPane(string command) => command is
        "session.type" or "session.write" or "session.text" or "session.copy" or "session.paste" or "session.search" or
        "session.status" or "session.metrics" or "session.output" or "session.readonly" or "session.agent-resume" or
        "surface.cursor" or "font" or "notify" or "command.run" ||
        command.StartsWith("font.", StringComparison.Ordinal) || command.StartsWith("image.", StringComparison.Ordinal) ||
        command.StartsWith("selection.", StringComparison.Ordinal);

    public static WebBridgeRequest Parse(string json, string token)
    {
        if (Encoding.UTF8.GetByteCount(json) > 6 * ReviewBridgeRequest.MaxValueBytes + 4096) throw new ArgumentException("bridge request too large");
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = doc.RootElement;
        ValidateObject(root, ["id", "token", "method", "options"]);
        string Required(JsonElement value, string key, int limit)
        {
            if (!value.TryGetProperty(key, out var item) || item.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(item.GetString()) || item.GetString()!.Length > limit)
                throw new ArgumentException("invalid bridge " + key);
            return item.GetString()!;
        }
        string id = Required(root, "id", 64), method = Required(root, "method", 128);
        if (Required(root, "token", 64) != token) throw new ArgumentException("stale bridge document");
        if (method is "zmx.present" or "zmx.reset" or "session.overlay.job.run" or "session.overlay.page.serve" or "session.companion.page.attach")
            throw new ArgumentException("command is not available from a page");
        var options = root.TryGetProperty("options", out var opt) ? opt : JsonSerializer.SerializeToElement(new { });
        ValidateObject(options, ["target", "window", "args"]);
        string? target = options.TryGetProperty("target", out _) ? Required(options, "target", 1024) : null;
        string? window = options.TryGetProperty("window", out _) ? Required(options, "window", 1024) : null;
        var args = options.TryGetProperty("args", out var supplied) ? supplied : JsonSerializer.SerializeToElement(new { });
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("bridge args must be an object");
        var keys = new HashSet<string>();
        foreach (var field in args.EnumerateObject()) if (!keys.Add(field.Name)) throw new ArgumentException("duplicate bridge argument");
        return new(id, method, target, window, args.Clone());
    }

    private static void ValidateObject(JsonElement value, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("bridge envelope must be an object");
        var keys = new HashSet<string>();
        foreach (var field in value.EnumerateObject())
            if (!keys.Add(field.Name) || !allowed.Contains(field.Name)) throw new ArgumentException("unknown or duplicate bridge field: " + field.Name);
    }
}
