using System.Text;
using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>The pinned page channel has no presentation generations or heartbeat frames.</summary>
public sealed record AgtermPageFrame(string Kind, long Id, string? Text = null, byte[]? Data = null);

public static class AgtermPageProtocol
{
    public const int MaxFrameBytes = 262144, MaxChunkBytes = 65536;
    public const int ResourceLimit = 8, BridgeLimit = 8;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static AgtermPageFrame Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length >= MaxFrameBytes || bytes.Span.Contains((byte)'\n'))
            throw new IOException("Invalid page frame length");
        try
        {
            _ = Utf8.GetCharCount(bytes.Span);
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 16 });
            var root = document.RootElement;
            AgtermRemoteDiscovery.RejectDuplicates(root);
            string kind = root.GetProperty("kind").GetString()!;
            long id = root.GetProperty("id").GetInt64();
            if (id <= 0) throw new IOException("Invalid page request ID");
            string? field = kind switch
            {
                "resource" => "path", "head" => "mimeType", "failure" => "error",
                "bridge" => "request", "reply" => "response", "chunk" => "data",
                "end" or "cancel" => null, _ => throw new IOException("Unknown page frame kind")
            };
            string? text = field is null ? null : root.GetProperty(field).GetString() ?? throw new IOException("Missing page payload");
            byte[]? data = kind == "chunk" ? Convert.FromBase64String(text!) : null;
            if (data?.Length > MaxChunkBytes) throw new IOException("Page chunk exceeds limit");
            return new(kind, id, data is null ? text : null, data);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or DecoderFallbackException or AgtermDiscoveryException)
        { throw new IOException("Malformed page frame", ex); }
    }

    public static byte[] Encode(AgtermPageFrame frame)
    {
        var fields = new Dictionary<string, object?> { ["kind"] = frame.Kind, ["id"] = frame.Id };
        string? field = frame.Kind switch
        {
            "resource" => "path", "head" => "mimeType", "failure" => "error", "bridge" => "request",
            "reply" => "response", "chunk" => "data", "end" or "cancel" => null,
            _ => throw new IOException("Unknown page frame kind")
        };
        if (field is not null) fields[field] = frame.Kind == "chunk" ? Convert.ToBase64String(frame.Data ?? []) : frame.Text;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(fields);
        _ = Decode(bytes);
        return [.. bytes, (byte)'\n'];
    }
}
