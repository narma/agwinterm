using System.Text;
using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>Wire envelopes only; decoding never authorizes an effect or an interaction.</summary>
public sealed record AgtermPresentationFrame(string Kind, long Generation, long Revision, JsonElement Body);
public sealed record AgtermPresentationHello(int Version, string[] Kinds, string Mode);

public static class AgtermPresentationProtocol
{
    public const int Version = 1;
    public const int MaxFrameBytes = 256 * 1024;
    public const int MaxPendingFrames = 256;
    public const string ResearchRevision = "c63e5158e637662ded7ba3a12eac4881b24caef1";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HashSet<string> EmptyKinds = ["ping", "ack", "presenter.acquire", "presenter.granted", "presenter.refused"];

    public static AgtermPresentationFrame Decode(ReadOnlyMemory<byte> line)
    {
        if (line.Length == 0 || line.Length > MaxFrameBytes || line.Span.Contains((byte)'\n'))
            throw new IOException("Invalid presentation frame length or delimiter");
        try
        {
            _ = Utf8.GetCharCount(line.Span);
            using var doc = JsonDocument.Parse(line, new() { MaxDepth = 32 });
            AgtermRemoteDiscovery.RejectDuplicates(doc.RootElement);
            var root = doc.RootElement;
            string kind = root.GetProperty("kind").GetString()!;
            if (string.IsNullOrEmpty(kind) || kind.Length > 128 || kind.Any(char.IsControl))
                throw new IOException("Invalid presentation kind");
            long gen = root.GetProperty("gen").GetInt64(), rev = root.GetProperty("rev").GetInt64();
            if (gen < 0 || rev < 0) throw new IOException("Invalid presentation ordering");
            string? key = PayloadKey(kind);
            JsonElement body = default;
            if (key is not null)
            {
                root.TryGetProperty(key, out body);
                bool nullable = kind is "status" or "context" or "hud" or "info";
                if (!(nullable && body.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    && body.ValueKind != (kind == "context" ? JsonValueKind.String : JsonValueKind.Object))
                    throw new IOException("Invalid presentation payload");
                if (body.ValueKind != JsonValueKind.Undefined) body = body.Clone();
            }
            var frame = new AgtermPresentationFrame(kind, gen, rev, body);
            if (kind == "hello") _ = Hello(frame);
            return frame;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or DecoderFallbackException or AgtermDiscoveryException)
        { throw new IOException("Malformed presentation frame", ex); }
    }

    public static AgtermPresentationHello Hello(AgtermPresentationFrame frame)
    {
        if (frame.Kind != "hello") throw new IOException("Expected presentation hello");
        var body = frame.Body;
        if (!body.TryGetProperty("version", out var rawVersion) || !rawVersion.TryGetInt32(out int version)
            || version < 1 || !body.TryGetProperty("mode", out var rawMode)
            || rawMode.ValueKind != JsonValueKind.String || rawMode.GetString() is not ("mirror" or "presenter")
            || !body.TryGetProperty("kinds", out var kinds) || kinds.ValueKind != JsonValueKind.Array
            || kinds.GetArrayLength() > 128) throw new IOException("Invalid presentation hello");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in kinds.EnumerateArray())
        {
            if (kind.ValueKind != JsonValueKind.String || kind.GetString() is not { Length: > 0 and <= 128 } name
                || name.Any(char.IsControl) || !names.Add(name)) throw new IOException("Invalid presentation capabilities");
        }
        return new(version, names.ToArray(), rawMode.GetString()!);
    }

    // Upstream versions express maximum supported wire version; positive newer peers include v1.
    public static int? NegotiateVersion(int peer) => peer >= 1 ? Version : null;
    public static IReadOnlySet<string> Intersect(AgtermPresentationHello peer, IEnumerable<string> implemented) =>
        peer.Kinds.Intersect(implemented, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);

    public static JsonElement NegotiatedSnapshot(JsonElement snapshot, IReadOnlySet<string> kinds)
    {
        if (snapshot.ValueKind != JsonValueKind.Object) throw new IOException("Invalid presentation snapshot");
        return JsonSerializer.SerializeToElement(snapshot.EnumerateObject()
            .Where(p => kinds.Contains(p.Name) && p.Name is "status" or "context" or "hud" or "layout" or "info")
            .ToDictionary(p => p.Name, p => p.Value.Clone()));
    }

    public static byte[] Encode(AgtermPresentationFrame frame)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", frame.Kind);
            writer.WriteNumber("gen", frame.Generation);
            writer.WriteNumber("rev", frame.Revision);
            if (PayloadKey(frame.Kind) is { } key && frame.Body.ValueKind != JsonValueKind.Undefined)
            { writer.WritePropertyName(key); frame.Body.WriteTo(writer); }
            writer.WriteEndObject();
        }
        if (buffer.Length >= MaxFrameBytes) throw new IOException("Presentation frame exceeds limit");
        var bytes = buffer.ToArray();
        _ = Decode(bytes);
        return [.. bytes, (byte)'\n'];
    }

    private static string? PayloadKey(string kind) => EmptyKinds.Contains(kind) ? null : kind switch
    {
        "hello" or "snapshot" or "status" or "context" or "hud" or "notify" or "sound" or "layout" or "info" => kind,
        "ask.request" or "ask.resolve" or "ask.rejected" or "ask.dismiss" => "ask",
        "overlay.request" or "overlay.rejected" or "overlay.close" or "overlay.resize" or "overlay.closed" => "overlay",
        "page.request" or "page.rejected" or "page.close" or "page.closed" or "page.command" or "page.state" => "page",
        "pane.respawn" => "respawn",
        _ => null // Additive kinds retain only their ordering; they never produce a payload effect.
    };
}

/// <summary>Incremental framing with a fixed memory ceiling, including unterminated hostile input.</summary>
public sealed class AgtermPresentationLines
{
    private readonly byte[] _line = new byte[AgtermPresentationProtocol.MaxFrameBytes];
    private int _length;
    private bool _failed;

    public void Feed(ReadOnlySpan<byte> bytes, Action<AgtermPresentationFrame> accept)
    {
        if (_failed) throw new IOException("Presentation decoder is closed");
        try
        {
            foreach (byte value in bytes)
            {
                if (value == '\n')
                {
                    var frame = AgtermPresentationProtocol.Decode(_line.AsMemory(0, _length));
                    _length = 0;
                    accept(frame);
                }
                else
                {
                    if (_length == _line.Length) throw new IOException("Presentation line exceeds limit");
                    _line[_length++] = value;
                }
            }
        }
        catch { _failed = true; throw; }
    }

    public void Complete()
    {
        _failed = true;
        if (_length != 0) throw new IOException("Truncated presentation line");
    }
}
