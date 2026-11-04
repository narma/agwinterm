using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>Origin-owned document fence. Call only on the page's serial UI authority.</summary>
public sealed class AgtermPageDocument
{
    public const string Capability = "page.bridge-revision";
    public const long MaxCounter = 9007199254740991;
    private long _revision, _applied, _minimumSequence;
    private bool _active;

    public void Invalidate(long commandSequence)
    {
        if (commandSequence <= _minimumSequence || commandSequence > MaxCounter)
            throw new IOException("Invalid page navigation sequence");
        _minimumSequence = commandSequence;
        _active = false;
    }

    public void Activate(AgtermPageDocumentRequest request, long issuedSequence)
    {
        if (request.Request is not null || request.Document <= _revision
            || request.AppliedSequence < _minimumSequence || request.AppliedSequence > issuedSequence)
            throw new IOException("Stale page document activation");
        _revision = request.Document;
        _applied = request.AppliedSequence;
        _active = true;
    }

    public void RequireCurrent(AgtermPageDocumentRequest request)
    {
        if (!_active || request.Document != _revision || request.AppliedSequence != _applied)
            throw new IOException("Page document retired");
    }
}

public sealed record AgtermPageDocumentRequest(long Document, long AppliedSequence, string? Request)
{
    public static AgtermPageDocumentRequest Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new() { MaxDepth = 32 });
            var root = doc.RootElement;
            AgtermRemoteDiscovery.RejectDuplicates(root);
            foreach (var field in root.EnumerateObject())
                if (field.Name is not ("document" or "appliedSeq" or "request")) throw new IOException("Invalid document envelope");
            long revision = root.GetProperty("document").GetInt64(), applied = root.GetProperty("appliedSeq").GetInt64();
            if (revision <= 0 || revision > AgtermPageDocument.MaxCounter || applied < 0 || applied > AgtermPageDocument.MaxCounter)
                throw new IOException("Invalid document counters");
            string? request = null;
            if (root.TryGetProperty("request", out var value))
            {
                if (value.ValueKind != JsonValueKind.Object) throw new IOException("Invalid document request");
                request = value.GetRawText();
            }
            return new(revision, applied, request);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or AgtermDiscoveryException)
        { throw new IOException("Malformed document envelope", ex); }
    }

    public string Encode() => Request is null
        ? JsonSerializer.Serialize(new { document = Document, appliedSeq = AppliedSequence })
        : JsonSerializer.Serialize(new { document = Document, appliedSeq = AppliedSequence, request = JsonSerializer.Deserialize<JsonElement>(Request) });
}
