using System.Text;
using System.Text.Json;

namespace Agwinterm.Core;

/// <summary>Deliberately independent of the terminal control dispatcher.</summary>
public sealed record ReviewBridgeRequest(string Id, string Method, string? Value)
{
    public const int MaxValueBytes = 1024 * 1024;
    public static ReviewBridgeRequest Parse(string json, string expectedToken)
    {
        if (Encoding.UTF8.GetByteCount(json) > 6 * MaxValueBytes + 4096) throw new ArgumentException("bridge payload is too large");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        Keys(root, "id", "token", "method", "options");
        string Text(JsonElement obj, string name) => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new ArgumentException(name + " must be a string");
        string id = Text(root, "id"), token = Text(root, "token"), method = Text(root, "method");
        if (id.Length is 0 or > 64 || expectedToken.Length == 0 || token != expectedToken) throw new ArgumentException("stale or invalid document binding");
        if (method is not ("session.overlay.submit" or "session.overlay.close")) throw new ArgumentException("review bridge permits only submit and close");
        if (!root.TryGetProperty("options", out var options)) throw new ArgumentException("options must be an object");
        Keys(options, "args");
        string? result = null;
        if (method == "session.overlay.submit")
        {
            if (!options.TryGetProperty("args", out var args)) throw new ArgumentException("submit requires args.value");
            Keys(args, "value"); result = Text(args, "value");
            if (Encoding.UTF8.GetByteCount(result) > MaxValueBytes) throw new ArgumentException("review value exceeds 1 MiB UTF-8");
        }
        else if (options.TryGetProperty("args", out var args)) Keys(args);
        return new(id, method, result);
    }
    private static void Keys(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ArgumentException("bridge fields must be objects");
        var seen = new HashSet<string>();
        foreach (var property in element.EnumerateObject())
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new ArgumentException("unsupported or duplicate bridge field: " + property.Name);
    }
}
