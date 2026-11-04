namespace Agwinterm.Pty;

/// <summary>One origin session's presenter authority; independent of terminal leadership and frame ordering.</summary>
public sealed class AgtermPresenterGrant
{
    public long? Holder { get; private set; }
    public long Generation { get; private set; }

    // Caller serializes changes with the origin broker's lock.
    public bool Acquire(long subscriber)
    {
        if (subscriber <= 0) throw new ArgumentOutOfRangeException(nameof(subscriber));
        if (Holder is { } current) return current == subscriber;
        Holder = subscriber; Generation = checked(Generation + 1); return true;
    }

    public bool Release(long subscriber)
    {
        if (Holder != subscriber) return false;
        Holder = null; Generation = checked(Generation + 1); return true;
    }

    public bool Authorizes(long subscriber, long owner) => Holder == subscriber && Generation == owner;
}
