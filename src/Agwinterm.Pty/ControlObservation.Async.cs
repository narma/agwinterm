namespace Agwinterm.Pty;

public sealed partial class ControlObservation
{
    public const int ObserverLimit = 128;
    private Func<long> _clock = () => Environment.TickCount64;
    internal void UseClock(Func<long> clock) => _clock = clock;
    private abstract class Pending(long? deadline)
    {
        public long? Deadline { get; } = deadline;
        public Action Evaluate { get; set; } = () => { };
        public abstract bool Completed { get; }
    }
    private sealed class Pending<T>(long? deadline) : Pending(deadline)
    {
        public TaskCompletionSource<T> Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool Completed => Source.Task.IsCompleted;
    }
    private readonly HashSet<Pending> _pending = new();
    internal int PendingCount { get { lock (_gate) return _pending.Count; } }

    private void NotifyPendingLocked()
    {
        foreach (var pending in _pending.ToArray())
        {
            pending.Evaluate();
            if (pending.Completed) _pending.Remove(pending);
        }
    }

    private Task<T> RegisterLocked<T>(Pending<T> pending, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_pending.Count >= ObserverLimit) throw Error("observer-limit", "too many pending observers");
        _pending.Add(pending);
        return AwaitPendingAsync(pending, cancellationToken);
    }

    private async Task<T> AwaitPendingAsync<T>(Pending<T> pending, CancellationToken cancellationToken)
    {
        using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            if (pending.Deadline is { } deadline)
            {
                while (!pending.Completed)
                {
                    long remaining;
                    lock (_gate)
                    {
                        pending.Evaluate();
                        remaining = Math.Max(1, deadline - _clock());
                    }
                    if (pending.Completed) break;
                    var timer = Task.Delay(TimeSpan.FromMilliseconds(remaining), timerCancellation.Token);
                    await Task.WhenAny(pending.Source.Task, timer).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            return await pending.Source.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await timerCancellation.CancelAsync().ConfigureAwait(false);
            lock (_gate) _pending.Remove(pending);
        }
    }

    public Task<EventPage> ReadAsync(EventQuery query, CancellationToken cancellationToken = default)
    {
        query.Validate();
        // Own a filter copy throughout the pending operation.
        query = query with { Kinds = query.Kinds?.ToHashSet(StringComparer.Ordinal) };
        lock (_gate)
        {
            var page = ReadLocked(query);
            if (query.Tail || query.WaitMs == 0 || page.Gap || page.Events.Count != 0) return Task.FromResult(page);
            var pending = new Pending<EventPage>(_clock() + query.WaitMs);
            pending.Evaluate = () =>
            {
                try
                {
                    var next = ReadLocked(query);
                    if (next.Gap || next.Events.Count != 0 || _clock() >= pending.Deadline)
                        pending.Source.TrySetResult(next);
                    else query = query with { Since = next.Cursor };
                }
                catch (ObservationException ex) { pending.Source.TrySetException(ex); }
            };
            return RegisterLocked(pending, cancellationToken);
        }
    }
}
