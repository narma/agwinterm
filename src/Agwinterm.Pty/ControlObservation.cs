using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>A window's bounded observation journal. All page metadata is sampled under one lock.</summary>
public sealed partial class ControlObservation
{
    public const int Retention = 1000;
    public const int MaxRecordBytes = 8192;
    public static string ProcessRun { get; } = Guid.NewGuid().ToString("N");
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "status", "notification", "session", "tree", "bind", "selection", "focus", "split",
        "scratch", "overlay", "hud", "session-status"
    };
    private readonly object _gate = new();
    private readonly Queue<(long Seq, string Type, JsonElement Record)> _records = new();
    private long _head;
    private bool _closed;
    public string Run { get; }
    public string Window { get; }
    public long LastSequence { get { lock (_gate) return _head; } }

    public ControlObservation(string window, string? run = null, long initialSequence = 0)
    {
        if (string.IsNullOrWhiteSpace(window) || window.Length > 256) throw new ArgumentException("Invalid window identity", nameof(window));
        Window = window;
        Run = run ?? ProcessRun;
        if (initialSequence < 0 || initialSequence == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(initialSequence));
        _head = initialSequence;
    }

    public long Publish(string type, IReadOnlyDictionary<string, object?>? fields = null)
    {
        lock (_gate)
        {
            if (_closed) return _head;
            if (!Kinds.Contains(type)) throw new ArgumentException("Unknown event kind: " + type);
            long seq = checked(_head + 1);
            var record = fields is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(fields);
            record["seq"] = seq;
            record["type"] = type;
            record["window"] = Window;
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record);
            if (bytes.Length > MaxRecordBytes && record.TryGetValue("info", out var info) && info is string text)
            {
                record["truncated"] = true;
                int length = text.Length;
                do
                {
                    length /= 2;
                    // Never retain half a UTF-16 surrogate pair.
                    if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
                    record["info"] = text[..length];
                    bytes = JsonSerializer.SerializeToUtf8Bytes(record);
                } while (bytes.Length > MaxRecordBytes && length > 0);
            }
            if (bytes.Length > MaxRecordBytes) throw new ArgumentException("Required event fields exceed the record bound");
            using var doc = JsonDocument.Parse(bytes);
            _records.Enqueue((seq, type, doc.RootElement.Clone()));
            _head = seq;
            while (_records.Count > Retention) _records.Dequeue();
            NotifyPendingLocked();
            return seq;
        }
    }

    public EventPage Read(EventQuery query)
    {
        query.Validate();
        lock (_gate) return ReadLocked(query);
    }

    private EventPage ReadLocked(EventQuery query)
    {
        if (_closed) throw Error("window-gone", "window closed");
        if (query.Run is not null && query.Run != Run) throw Error("run-mismatch", "application run changed");
        long since = query.Tail ? _head : query.Since;
        if (since > _head) throw Error("cursor-ahead", "cursor is ahead of the journal");
        long oldest = _records.Count == 0 ? _head + 1 : _records.Peek().Seq;
        var events = new List<JsonElement>();
        long cursor = since;
        int limit = query.Limit == 0 ? Retention : query.Limit;
        foreach (var record in _records)
        {
            if (record.Seq <= since) continue;
            cursor = record.Seq;
            if (query.Kinds is not null && !query.Kinds.Contains(record.Type)) continue;
            events.Add(record.Record);
            if (events.Count == limit) break;
        }
        if (events.Count < limit) cursor = _head;
        return new(Run, Window, oldest, _head, cursor, since < oldest - 1, cursor < _head, events);
    }

    private ObservationException Error(string code, string message) => new(code, message, Run, Window);

    public void Close()
    {
        IDisposable[] subscriptions;
        lock (_gate)
        {
            _closed = true;
            NotifyPendingLocked();
            subscriptions = _subscriptions.Values.ToArray();
            _subscriptions.Clear();
        }
        foreach (var subscription in subscriptions) subscription.Dispose();
    }
}

public sealed record EventQuery(long Since = 0, int Limit = 0, string? Run = null,
    IReadOnlySet<string>? Kinds = null, bool Tail = false, int WaitMs = 0)
{
    public static EventQuery Parse(JsonElement args)
    {
        if (args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new();
        if (args.ValueKind != JsonValueKind.Object) throw new ObservationException("invalid-argument", "events args must be an object");
        long since = 0;
        int limit = 0, wait = 0;
        string? run = null;
        bool tail = false, hasSince = false;
        HashSet<string>? kinds = null;
        var seen = new HashSet<string>();
        foreach (var prop in args.EnumerateObject())
        {
            if (!seen.Add(prop.Name)) throw new ObservationException("invalid-argument", "duplicate event argument: " + prop.Name);
            var value = prop.Value;
            switch (prop.Name)
            {
                case "since" when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out since): hasSince = true; break;
                case "limit" when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out limit): break;
                case "wait-ms" when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out wait): break;
                case "run" when value.ValueKind == JsonValueKind.String: run = value.GetString(); break;
                case "tail" when value.ValueKind is JsonValueKind.True or JsonValueKind.False: tail = value.GetBoolean(); break;
                case "kinds" when value.ValueKind == JsonValueKind.Array:
                    kinds = new(StringComparer.Ordinal);
                    foreach (var kind in value.EnumerateArray())
                        if (kind.ValueKind != JsonValueKind.String || !kinds.Add(kind.GetString()!))
                            throw new ObservationException("invalid-argument", "kinds must be unique strings");
                    break;
                default: throw new ObservationException("invalid-argument", "invalid event argument: " + prop.Name);
            }
        }
        if (tail && hasSince) throw new ObservationException("invalid-argument", "tail and since are mutually exclusive");
        var query = new EventQuery(since, limit, run, kinds, tail, wait);
        query.Validate();
        return query;
    }

    public void Validate()
    {
        if (Since < 0 || Limit < 0 || Limit > ControlObservation.Retention || WaitMs < 0 || WaitMs > 25000 || (Tail && Since != 0))
            throw new ObservationException("invalid-argument", "invalid event cursor, limit, tail or wait-ms");
        if (Run is not null && string.IsNullOrWhiteSpace(Run)) throw new ObservationException("invalid-argument", "run must be nonempty");
        if (Kinds is not null && (Kinds.Count == 0 || Kinds.Any(k => !ControlObservation.Kinds.Contains(k))))
            throw new ObservationException("invalid-argument", "unknown or empty event kinds");
    }
}

public sealed record EventPage(string Run, string Window, long Oldest, long Head, long Cursor,
    bool Gap, bool More, IReadOnlyList<JsonElement> Events)
{
    public string ToJson() => JsonSerializer.Serialize(new
    {
        schema = 1, run = Run, window = Window, oldest = Oldest, head = Head,
        cursor = Cursor, gap = Gap, more = More, events = Events
    });
}

public sealed class ObservationException(string code, string message, string? run = null, string? window = null) : Exception(message)
{
    public string Code { get; } = code;
    public string ToJson() => JsonSerializer.Serialize(new { ok = false, code = Code, error = Message, run, window });
}
