using Agwinterm.Core;

namespace Agwinterm.Pty;

public sealed record StatusSnapshot(AgentStatus Status, bool Blink, bool AutoReset, long ChangedAt, long Version);

/// <summary>Serializes status writes and atomic snapshot/subscription establishment. Observers must
/// only copy/publish the immutable value; they must never wait for UI, I/O or another status signal.</summary>
public sealed class StatusSignal
{
    private readonly object _gate = new();
    private StatusSnapshot _value = new(AgentStatus.Idle, false, false, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 0);
    private event Action<StatusSnapshot, bool>? _written;
    public StatusSnapshot Snapshot { get { lock (_gate) return _value; } }

    public IDisposable Subscribe(Action<StatusSnapshot, bool> observer)
    {
        lock (_gate)
        {
            observer(_value, false);
            _written += observer;
        }
        return new Subscription(this, observer);
    }

    public bool Set(AgentStatus status, bool blink, bool autoReset)
    {
        lock (_gate)
        {
            blink &= status != AgentStatus.Idle;
            autoReset &= status != AgentStatus.Idle;
            bool changed = _value.Status != status || _value.Blink != blink || _value.AutoReset != autoReset;
            _value = new(status, blink, autoReset, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), checked(_value.Version + 1));
            _written?.Invoke(_value, changed);
            return changed;
        }
    }

    internal void Backdate(long stamp) { lock (_gate) _value = _value with { ChangedAt = stamp }; }

    private sealed class Subscription(StatusSignal signal, Action<StatusSnapshot, bool> observer) : IDisposable
    {
        private StatusSignal? _signal = signal;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _signal, null) is { } owner)
                lock (owner._gate) owner._written -= observer;
        }
    }
}
