namespace Agwinterm.Pty;

/// <summary>One asynchronous recovery per binding; cancellation invalidates every queued callback.</summary>
public sealed class WslRecoveryGate
{
    private readonly object _sync = new();
    private long _generation;
    private long? _active;
    private bool _closed;

    public bool TryBegin(out long generation)
    {
        lock (_sync)
        {
            generation = 0;
            if (_closed || _active is not null) return false;
            generation = ++_generation;
            _active = generation;
            return true;
        }
    }

    public bool IsCurrent(long generation)
    {
        lock (_sync) return !_closed && _active == generation && _generation == generation;
    }

    public bool Publish(long generation, Action publish)
    {
        lock (_sync)
        {
            if (!IsCurrent(generation)) return false;
            publish();
            return true;
        }
    }

    public void Complete(long generation)
    {
        lock (_sync) if (_active == generation) _active = null;
    }

    public void Cancel()
    {
        lock (_sync) { _closed = true; ++_generation; }
    }

    public void Reopen()
    {
        lock (_sync)
        {
            if (_active is not null) throw new InvalidOperationException("Recovery is still completing");
            _closed = false;
        }
    }
}

public sealed class WslRecoveryException(string outcome, string message) : Exception(message)
{
    public string Outcome { get; } = outcome is "missing" or "unsupported" or "unavailable" ? outcome : "uncertain";
}
