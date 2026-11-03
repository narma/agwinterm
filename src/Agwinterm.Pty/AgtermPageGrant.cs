namespace Agwinterm.Pty;

/// <summary>A single-use page capability; channel/presenter loss ends it, including after a claim.</summary>
public sealed class AgtermPageGrant
{
    private readonly object _gate = new();
    private readonly long _deadline;
    private bool _claimed, _ended;
    public string Page { get; } = Guid.NewGuid().ToString("D");
    public string Token { get; }
    public AgtermOriginPresentationScope Scope { get; }
    public long Subscriber { get; }
    public long Owner { get; }
    public bool NeedsChannel { get; }
    public event Action? Ended;
    public bool IsAlive { get { lock (_gate) return !_ended; } }
    public bool IsClaimed { get { lock (_gate) return _claimed; } }

    public AgtermPageGrant(AgtermOriginPresentationScope scope, long subscriber, long owner, bool needsChannel, long now)
    {
        if (!scope.Origin.IsValid || string.IsNullOrWhiteSpace(scope.Account) || !Guid.TryParseExact(scope.Session, "D", out _)
            || !Guid.TryParseExact(scope.Window, "D", out _) || subscriber <= 0 || owner <= 0)
            throw new ArgumentException("Invalid origin page scope");
        Scope = scope; Subscriber = subscriber; Owner = owner; NeedsChannel = needsChannel;
        Token = scope.Session + "." + Guid.NewGuid().ToString("N");
        _deadline = checked(now + 30000);
    }

    public void Claim(AgtermOriginPresentationScope scope, long subscriber, long owner, long now)
    {
        lock (_gate)
        {
            if (!NeedsChannel || _ended || _claimed || now >= _deadline || scope != Scope || subscriber != Subscriber || owner != Owner)
                throw new IOException("Page claim expired, used or unauthorized");
            _claimed = true;
        }
    }

    public void Expire(long now)
    {
        bool end;
        lock (_gate) { end = !_ended && NeedsChannel && !_claimed && now >= _deadline; if (end) _ended = true; }
        if (end) Ended?.Invoke();
    }

    public void Close()
    {
        lock (_gate) { if (_ended) return; _ended = true; }
        Ended?.Invoke();
    }
}
