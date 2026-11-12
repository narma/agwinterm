namespace Agwinterm.Core;

public sealed record AgentAttention(AgentStatus Status, AgentStatus RawStatus, bool Seen, long ChangedAt);

/// <summary>Viewer-local attention state, separate from a producer's status and its lifetime.</summary>
public sealed class AgentActivity
{
    public AgentStatus Status { get; private set; }
    public long ChangedAt { get; private set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public bool Reported { get; private set; }
    private AgentStatus _raw;
    private bool _seen;
    public AgentAttention? Capture() => Reported ? new(Status, _raw, _seen, ChangedAt) : null;
    public bool Restore(AgentAttention? saved)
    {
        if (saved is null || !Enum.IsDefined(saved.Status) || !Enum.IsDefined(saved.RawStatus)
            || saved.ChangedAt < 0) return false;
        Status = saved.Status;
        _raw = saved.RawStatus;
        _seen = saved.Seen;
        ChangedAt = saved.ChangedAt;
        Reported = true;
        return true;
    }
    public void Reset()
    {
        Reported = false;
        Status = _raw = AgentStatus.Idle;
        _seen = false;
    }
    public void Observe(AgentStatus status, bool viewed)
    {
        bool changed = !Reported || status != _raw;
        bool finished = status == AgentStatus.Completed || status == AgentStatus.Idle && _raw == AgentStatus.Active;
        if (changed)
        {
            if (finished) { Status = AgentStatus.Completed; _seen = viewed; }
            else if (!(status == AgentStatus.Idle && Status == AgentStatus.Completed))
            { Status = status; _seen = false; }
            if (!(status == AgentStatus.Idle && _raw == AgentStatus.Completed))
                ChangedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        Reported = true; _raw = status;
        if (viewed) _seen = true;
    }
    public AgentStatus Display(bool viewed)
    {
        if (viewed) _seen = true;
        return Status == AgentStatus.Completed && _seen ? AgentStatus.Idle : Status;
    }
    public static int Priority(AgentStatus status) => status switch
    { AgentStatus.Blocked => 0, AgentStatus.Completed => 1, AgentStatus.Active => 2, _ => 3 };
}
