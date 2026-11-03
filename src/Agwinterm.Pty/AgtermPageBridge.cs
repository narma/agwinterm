using System.Text;
using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>Remote file-page authority. No caller-supplied routing reaches the control dispatcher.</summary>
public sealed record AgtermPageBridge(string Command, JsonElement Args)
{
    public static AgtermPageBridge Parse(string json, bool review)
    {
        if (Encoding.UTF8.GetByteCount(json) >= AgtermPageProtocol.MaxFrameBytes) throw new IOException("Page bridge request too large");
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            AgtermRemoteDiscovery.RejectDuplicates(root);
            Keys(root, ["cmd", "args"]);
            string command = root.GetProperty("cmd").GetString() ?? throw new IOException("Missing page command");
            string[] allowed = command switch
            {
                "session.overlay.submit" => ["value"],
                "session.overlay.close" => [],
                "session.overlay.hide" or "session.overlay.show" or "session.overlay.toggle" when !review => [],
                "session.overlay.resize" when !review => ["size-percent", "width", "height"],
                "session.overlay.reload" when !review => ["current"],
                "session.overlay.navigate" when !review => ["direction"],
                "session.text" when !review => ["all", "lines", "styles"],
                "session.output" when !review => [],
                "session.type" when !review => ["text", "allow-control"],
                "session.write" when !review => ["text"],
                "session.status" when !review => ["status", "blink", "auto-reset", "sound"],
                _ => throw new IOException("Command is outside the page grant")
            };
            var args = root.TryGetProperty("args", out var value) && value.ValueKind != JsonValueKind.Null
                ? value : JsonSerializer.SerializeToElement(new { });
            Keys(args, allowed);
            if (command == "session.overlay.submit" && (!args.TryGetProperty("value", out var submitted) || submitted.ValueKind != JsonValueKind.String))
                throw new IOException("Page submit requires a string value");
            if (command == "session.overlay.navigate" && (!args.TryGetProperty("direction", out var direction)
                || direction.ValueKind != JsonValueKind.String || direction.GetString() is not ("back" or "forward" or "browser")))
                throw new IOException("Unsupported remote page navigation");
            return new(command, args.Clone());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or AgtermDiscoveryException)
        { throw new IOException("Malformed page bridge request", ex); }
    }

    private static void Keys(JsonElement value, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new IOException("Page bridge object required");
        foreach (var field in value.EnumerateObject())
            if (!allowed.Contains(field.Name, StringComparer.Ordinal)) throw new IOException("Page bridge field is not authorized");
    }
}
