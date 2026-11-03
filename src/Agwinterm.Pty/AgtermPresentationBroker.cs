using System.Text.Json;
using System.Threading.Channels;

namespace Agwinterm.Pty;

public sealed record AgtermOriginPresentationScope(string Account, string Window, string Session, WslOriginScope Origin);

/// <summary>Origin-owned coherent snapshots and subscriptions. No command forwarding or terminal authority.</summary>
public sealed class AgtermPresentationBroker
{
    private readonly object _gate = new();
    private readonly Dictionary<AgtermOriginPresentationScope, Entry> _entries = [];
    private long _generation;
    private sealed class Entry(JsonElement snapshot, string[] kinds, bool presenterReady)
    {
        public JsonElement Snapshot = snapshot;
        public readonly string[] Kinds = kinds;
        public readonly List<Subscription> Subscribers = [];
        public readonly bool PresenterReady = presenterReady;
        public readonly AgtermPresenterGrant Grant = new();
    }

    public void Register(AgtermOriginPresentationScope scope, JsonElement snapshot, string[] implemented, bool presenterReady = false)
    {
        if (string.IsNullOrWhiteSpace(scope.Account) || !Guid.TryParse(scope.Window, out _) || !Guid.TryParse(scope.Session, out _)
            || !scope.Origin.IsValid || snapshot.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Invalid presentation origin scope or snapshot");
        _ = AgtermPresentationProtocol.Encode(new("snapshot", 0, 0, snapshot));
        lock (_gate)
        {
            if (_entries.TryGetValue(scope, out var current))
            {
                if (!current.Kinds.SequenceEqual(implemented.Distinct(StringComparer.Ordinal)) || current.PresenterReady != presenterReady)
                    throw new InvalidOperationException("Presentation capabilities changed without retirement");
                current.Snapshot = snapshot.Clone();
                return;
            }
            _entries.Add(scope, new(snapshot.Clone(), implemented.Distinct(StringComparer.Ordinal).ToArray(), presenterReady));
        }
    }

    public Subscription Subscribe(AgtermOriginPresentationScope scope, AgtermPresentationHello hello)
    {
        if (AgtermPresentationProtocol.NegotiateVersion(hello.Version) is null) throw new IOException("Unsupported presentation version");
        lock (_gate)
        {
            if (!_entries.TryGetValue(scope, out var entry)) throw new IOException("Exact presentation origin unavailable");
            if (entry.Subscribers.Count >= 16) throw new IOException("Too many presentation subscribers");
            var kinds = AgtermPresentationProtocol.Intersect(hello, entry.Kinds);
            string mode = entry.PresenterReady && hello.Mode == "presenter" ? "presenter" : "mirror";
            var subscription = new Subscription(++_generation, kinds, mode, s => Unsubscribe(scope, s), s => AcquirePresenter(scope, s));
            // All snapshots/publications share this lock: no delta can overtake initial state.
            subscription.Offer("hello", JsonSerializer.SerializeToElement(new { version = 1, kinds, mode }));
            subscription.Offer("snapshot", AgtermPresentationProtocol.NegotiatedSnapshot(entry.Snapshot, kinds));
            entry.Subscribers.Add(subscription);
            return subscription;
        }
    }

    public void Publish(AgtermOriginPresentationScope scope, JsonElement snapshot, string kind, JsonElement payload, bool mirrored = false)
    {
        if (mirrored) return;
        if (snapshot.ValueKind != JsonValueKind.Object) throw new ArgumentException("Invalid snapshot");
        _ = AgtermPresentationProtocol.Encode(new("snapshot", 0, 0, snapshot));
        _ = AgtermPresentationProtocol.Encode(new(kind, 0, 0, payload));
        lock (_gate)
        {
            if (!_entries.TryGetValue(scope, out var entry)) throw new IOException("Presentation origin retired");
            if (!entry.Kinds.Contains(kind, StringComparer.Ordinal)) throw new IOException("Origin kind is not implemented");
            entry.Snapshot = snapshot.Clone();
            foreach (var subscriber in entry.Subscribers.ToArray())
                if (subscriber.Kinds.Contains(kind) && !subscriber.Offer(kind, payload))
                { entry.Subscribers.Remove(subscriber); entry.Grant.Release(subscriber.Generation); subscriber.Complete(); }
        }
    }

    public void Retire(AgtermOriginPresentationScope scope)
    {
        lock (_gate)
            if (_entries.Remove(scope, out var entry))
                foreach (var subscriber in entry.Subscribers) { entry.Grant.Release(subscriber.Generation); subscriber.Complete(); }
    }

    public bool SendSound(AgtermOriginPresentationScope scope, string? name, bool mirrored = false)
    {
        if (mirrored) return false;
        lock (_gate)
        {
            if (Presenter(scope) is not { } presenter || !presenter.Subscriber.Kinds.Contains("sound")) return false;
            return SendPresenter(scope, presenter.Subscriber, presenter.Owner, "sound", JsonSerializer.SerializeToElement(new { name }));
        }
    }

    private bool AcquirePresenter(AgtermOriginPresentationScope scope, Subscription subscriber)
    {
        lock (_gate)
        {
            bool granted = _entries.TryGetValue(scope, out var entry) && entry.Subscribers.Contains(subscriber)
                && entry.PresenterReady && subscriber.Mode == "presenter" && entry.Grant.Acquire(subscriber.Generation);
            if (!subscriber.Offer(granted ? "presenter.granted" : "presenter.refused", default))
            { Unsubscribe(scope, subscriber); throw new IOException("Presentation subscriber stalled"); }
            return granted;
        }
    }

    public long? PresenterOwner(AgtermOriginPresentationScope scope, Subscription subscriber)
    {
        lock (_gate) return _entries.TryGetValue(scope, out var entry) && entry.Subscribers.Contains(subscriber)
            && entry.Grant.Holder == subscriber.Generation ? entry.Grant.Generation : null;
    }

    public (Subscription Subscriber, long Owner)? Presenter(AgtermOriginPresentationScope scope)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(scope, out var entry) || entry.Grant.Holder is not { } holder) return null;
            var subscriber = entry.Subscribers.SingleOrDefault(s => s.Generation == holder);
            return subscriber is null ? null : (subscriber, entry.Grant.Generation);
        }
    }

    public bool SendPresenter(AgtermOriginPresentationScope scope, Subscription subscriber, long owner, string kind, JsonElement payload)
    {
        lock (_gate)
        {
            if (!Authorizes(scope, subscriber, owner)) return false;
            _ = AgtermPresentationProtocol.Encode(new(kind, subscriber.Generation, 0, payload));
            if (subscriber.Offer(kind, payload)) return true;
            Unsubscribe(scope, subscriber); return false;
        }
    }

    public bool Authorizes(AgtermOriginPresentationScope scope, Subscription subscriber, long owner)
    {
        lock (_gate) return _entries.TryGetValue(scope, out var entry) && entry.Subscribers.Contains(subscriber)
            && entry.Grant.Authorizes(subscriber.Generation, owner);
    }

    private void Unsubscribe(AgtermOriginPresentationScope scope, Subscription subscriber)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(scope, out var entry))
            { entry.Subscribers.Remove(subscriber); entry.Grant.Release(subscriber.Generation); }
            subscriber.Complete();
        }
    }

    public sealed class Subscription : IDisposable
    {
        public long Generation { get; }
        public IReadOnlySet<string> Kinds { get; }
        public string Mode { get; }
        private readonly Action<Subscription> _close;
        private readonly Func<Subscription, bool> _acquire;
        private readonly Channel<AgtermPresentationFrame> _frames = Channel.CreateBounded<AgtermPresentationFrame>(
            new BoundedChannelOptions(AgtermPresentationProtocol.MaxPendingFrames) { FullMode = BoundedChannelFullMode.Wait });
        private long _revision;
        private int _closed;
        public event Action? Closed;
        internal Subscription(long generation, IReadOnlySet<string> kinds, string mode, Action<Subscription> close, Func<Subscription, bool> acquire)
        { Generation = generation; Kinds = kinds; Mode = mode; _close = close; _acquire = acquire; }
        public bool AcquirePresenter() => _acquire(this);
        internal bool Offer(string kind, JsonElement payload)
        {
            lock (_frames) return _frames.Writer.TryWrite(new(kind, Generation, _revision++,
                payload.ValueKind == JsonValueKind.Undefined ? default : payload.Clone()));
        }
        internal void Complete()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _frames.Writer.TryComplete(); Closed?.Invoke();
        }
        public IAsyncEnumerable<AgtermPresentationFrame> ReadAllAsync(CancellationToken ct) => _frames.Reader.ReadAllAsync(ct);
        public void Dispose() => _close(this);
    }
}
