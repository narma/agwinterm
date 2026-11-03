using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>One origin-owned reservation. All transitions are serialized, including claim versus loss.</summary>
public sealed class AgtermOverlayJob
{
    private readonly object _gate = new();
    public string Id { get; }
    public AgtermOriginPresentationScope Scope { get; }
    public long Subscriber { get; }
    public long Owner { get; }
    public string? Pane { get; }
    private readonly JsonElement _context;
    private readonly long _deadline;
    private bool _claimed, _surfaceGone, _cancelled;
    public bool Wait { get; }
    private string? _result;
    private readonly TaskCompletionSource<string> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<string> Completion => _done.Task;
    public string? Result { get { lock (_gate) return _result; } }
    public bool Claimed { get { lock (_gate) return _claimed; } }
    public JsonElement LaunchContext { get { lock (_gate) return _claimed ? _context.Clone() : throw new IOException("Job not claimed"); } }
    public bool Occupied { get { lock (_gate) return _result is null || Wait && !_surfaceGone && _claimed; } }
    public event Action? Cancelled;

    public AgtermOverlayJob(AgtermOriginPresentationScope scope, long subscriber, long owner, string? pane,
        JsonElement context, bool wait, long now)
    {
        if (!scope.Origin.IsValid || !Guid.TryParse(scope.Session, out _) || !Guid.TryParse(scope.Window, out _)
            || string.IsNullOrEmpty(scope.Account) || subscriber <= 0 || owner <= 0
            || context.ValueKind != JsonValueKind.Object || JsonSerializer.SerializeToUtf8Bytes(context).Length > 128 * 1024)
            throw new ArgumentException("Invalid or oversized origin job context");
        Id = scope.Session + "." + Guid.NewGuid().ToString("N");
        Scope = scope; Subscriber = subscriber; Owner = owner; Pane = pane;
        _context = context.Clone(); Wait = wait; _deadline = checked(now + 15000);
    }

    public JsonElement Claim(AgtermOriginPresentationScope scope, long subscriber, long owner, long now)
    {
        lock (_gate)
        {
            if (scope != Scope || subscriber != Subscriber || owner != Owner || _claimed || _result is not null || now >= _deadline)
                throw new IOException("Job claim expired, used or unauthorized");
            _claimed = true;
            return _context.Clone();
        }
    }

    public void Expire(long now)
    { lock (_gate) if (!_claimed && now >= _deadline) Finish("expired"); }

    public void PresentationLost()
    {
        lock (_gate)
        {
            _surfaceGone = true;
            if (!_claimed) Finish("presenter-lost");
        }
    }

    public void Close(string reason = "closed")
    {
        lock (_gate) { _surfaceGone = true; Finish(reason); if (_cancelled) return; _cancelled = true; }
        Cancelled?.Invoke();
    }

    public void Finish(string result)
    { lock (_gate) { if (_result is not null) return; _result = result; _done.TrySetResult(result); } }
}
