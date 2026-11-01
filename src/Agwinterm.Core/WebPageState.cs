namespace Agwinterm.Core;

/// <summary>Ephemeral document identity and presentation, with no browser or platform objects.</summary>
public class WebPageState(OverlayGeometry geometry)
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public OverlayGeometry Geometry { get; set; } = geometry;
    public bool Hidden { get; set; }
    public bool Visible { get; set; }
    public string State { get; set; } = "loading";
    public string? Error { get; set; }
    public string? CurrentUrl { get; set; }
    public double Zoom { get; set; } = 1;
    public long DocumentGeneration { get; private set; }
    public void BeginDocument() { DocumentGeneration++; State = "loading"; Error = null; }
}

public sealed record WebPageOutcome(string PageId, string Outcome, string? Value = null);

/// <summary>Instance-wide results; pending entries never consume the finished retention budget.</summary>
public sealed class WebPageOutcomes
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WebPageOutcome> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _finished = new();
    public const int FinishedLimit = 32;
    public void Register(string id)
    {
        lock (_gate) _entries.Add(id, new(id, "pending"));
    }
    public WebPageOutcome Read(string id)
    {
        lock (_gate) return _entries.TryGetValue(id, out var value) ? value : throw new ArgumentException("unknown or evicted page ID");
    }
    public bool Complete(string id, string? value)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var current) || current.Outcome != "pending") return false;
            _entries[id] = new(id, value is null ? "dismissed" : "submitted", value);
            _finished.Enqueue(id);
            while (_finished.Count > FinishedLimit) _entries.Remove(_finished.Dequeue());
            return true;
        }
    }
}
