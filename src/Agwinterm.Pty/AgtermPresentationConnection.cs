namespace Agwinterm.Pty;

public enum AgtermPresentationPhase { Unsupported, Connecting, AwaitingSnapshot, Connected, Failed, Closed }
public enum AgtermPresentationDisposition { Ignored, Control, Snapshot, Delta }

/// <summary>Pure viewer ordering and retry policy. Launch tokens are local, never wire generations.</summary>
public sealed class AgtermPresentationConnection
{
    public AgtermPresentationPhase Phase { get; private set; } = AgtermPresentationPhase.Closed;
    public long Launch { get; private set; }
    public long? Generation { get; private set; }
    public long Revision { get; private set; } = -1;
    public IReadOnlySet<string> Kinds { get; private set; } = new HashSet<string>();
    public string? Error { get; private set; }
    private int _retries;
    private DateTimeOffset _started, _lastReceived;
    private string[] _implemented = [];

    public long Begin(int? advertisedVersion, IEnumerable<string> implemented, DateTimeOffset now)
    {
        Launch++;
        Generation = null;
        Revision = -1;
        Kinds = new HashSet<string>();
        Error = null;
        _started = _lastReceived = now;
        _implemented = implemented.Distinct(StringComparer.Ordinal).ToArray();
        Phase = advertisedVersion is > 0 ? AgtermPresentationPhase.Connecting : AgtermPresentationPhase.Unsupported;
        return Launch;
    }

    public AgtermPresentationDisposition Accept(long launch, AgtermPresentationFrame frame, DateTimeOffset now)
    {
        if (launch != Launch || Phase is AgtermPresentationPhase.Closed or AgtermPresentationPhase.Failed or AgtermPresentationPhase.Unsupported)
            return AgtermPresentationDisposition.Ignored;
        if (Phase == AgtermPresentationPhase.Connecting)
        {
            if (frame.Kind != "hello") throw new IOException("Presentation did not start with hello");
            var hello = AgtermPresentationProtocol.Hello(frame);
            Generation = frame.Generation;
            Kinds = AgtermPresentationProtocol.Intersect(hello, _implemented);
            Phase = AgtermPresentationPhase.AwaitingSnapshot;
        }
        else
        {
            if (frame.Generation != Generation || frame.Revision <= Revision) return AgtermPresentationDisposition.Ignored;
            if (frame.Kind == "hello") throw new IOException("Repeated presentation hello");
            if (Phase == AgtermPresentationPhase.AwaitingSnapshot && frame.Kind is not ("snapshot" or "ping" or "ack"))
                throw new IOException("Presentation delta preceded snapshot");
        }
        Revision = frame.Revision;
        _lastReceived = now;
        if (frame.Kind == "snapshot")
        {
            if (Phase != AgtermPresentationPhase.AwaitingSnapshot) throw new IOException("Unexpected presentation snapshot");
            Phase = AgtermPresentationPhase.Connected;
            _retries = 0;
            return AgtermPresentationDisposition.Snapshot;
        }
        if (frame.Kind is "hello" or "ping" or "ack") return AgtermPresentationDisposition.Control;
        // Presenter role frames are independent of supportedKinds in upstream v1.
        if (frame.Kind is "presenter.granted" or "presenter.refused" or "ask.request" or "ask.dismiss"
            or "overlay.request" or "overlay.close" or "overlay.resize") return AgtermPresentationDisposition.Delta;
        return Kinds.Contains(frame.Kind) ? AgtermPresentationDisposition.Delta : AgtermPresentationDisposition.Ignored;
    }

    public bool Expired(long launch, DateTimeOffset now, TimeSpan handshake, TimeSpan silence) =>
        launch == Launch && (Phase is AgtermPresentationPhase.Connecting or AgtermPresentationPhase.AwaitingSnapshot
            ? now - _started >= handshake : Phase == AgtermPresentationPhase.Connected && now - _lastReceived >= silence);

    public TimeSpan? Fail(long launch, string reason)
    {
        if (launch != Launch || Phase is AgtermPresentationPhase.Closed or AgtermPresentationPhase.Unsupported or AgtermPresentationPhase.Failed) return null;
        Phase = AgtermPresentationPhase.Failed;
        Error = reason;
        return _retries < 5 ? TimeSpan.FromSeconds(1 << _retries++) : null;
    }

    public void Close() { Launch++; Phase = AgtermPresentationPhase.Closed; Generation = null; Kinds = new HashSet<string>(); }
}
