namespace Agwinterm.Pty;

internal enum JournalEventKind : byte { Output=1, Resize=2 }

/// <summary>Immutable owned bytes. Reading an event never acknowledges or trims it.</summary>
internal sealed record JournalEvent(long Sequence, JournalEventKind Kind, ReadOnlyMemory<byte> Payload)
{
    public int RetainedBytes => checked(Payload.Length+32);
}
internal sealed record JournalCheckpoint(long Sequence, ReadOnlyMemory<byte> Snapshot);

/// <summary>Parser-free bounded event history. The PTY producer may block at the
/// cap; checkpoint persistence and consumers never need its mutation lock.</summary>
internal sealed class TerminalJournal : IDisposable
{
    internal const int MaximumOutputBytes=64*1024;
    internal const int MaximumCheckpointBytes=64*1024*1024;
    private readonly object _gate=new();
    private readonly Dictionary<long,JournalEvent> _events=new();
    private readonly SemaphoreSlim _checkpointGate=new(1,1);
    private TaskCompletionSource _changed=NewSignal();
    private JournalCheckpoint _checkpoint=new(0,ReadOnlyMemory<byte>.Empty);
    private long _sequence;
    private long _firstSequence=1;
    private int _retained;
    private int _peakRetained;
    private bool _completed, _disposed;
    public string Incarnation { get; }
    public int CapacityBytes { get; }
    public int RetainedBytes { get { lock(_gate) return _retained; } }
    public int PeakRetainedBytes { get { lock(_gate) return _peakRetained; } }
    public long LastSequence { get { lock(_gate) return _sequence; } }
    public JournalCheckpoint Checkpoint { get { lock(_gate) { ThrowIfDisposed(); return _checkpoint; } } }

    public TerminalJournal(string incarnation,int capacityBytes=64*1024*1024)
    {
        if(string.IsNullOrWhiteSpace(incarnation) || incarnation.Length > 128) throw new ArgumentException("Invalid incarnation.",nameof(incarnation));
        if(capacityBytes < 33) throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        Incarnation=incarnation; CapacityBytes=capacityBytes;
    }
    public long AppendOutput(ReadOnlySpan<byte> bytes,CancellationToken cancellation=default)
    {
        if(bytes.Length is < 1 or > MaximumOutputBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        return Append(JournalEventKind.Output,bytes,cancellation);
    }
    public long AppendResize(int columns,int rows,CancellationToken cancellation=default)
    {
        if(columns is < 1 or > 10000 || rows is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(columns));
        Span<byte> payload=stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload,columns);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload[4..],rows);
        return Append(JournalEventKind.Resize,payload,cancellation);
    }
    private long Append(JournalEventKind kind,ReadOnlySpan<byte> bytes,CancellationToken cancellation)
    {
        int cost=checked(bytes.Length+32);
        if(cost > CapacityBytes) throw new ArgumentOutOfRangeException(nameof(bytes),"Event exceeds journal capacity.");
        using var registration=cancellation.UnsafeRegister(static state=>
        {
            var journal=(TerminalJournal)state!;
            lock(journal._gate) Monitor.PulseAll(journal._gate);
        },this);
        lock(_gate)
        {
            while(true)
            {
                cancellation.ThrowIfCancellationRequested(); ThrowIfDisposed();
                if(_completed) throw new InvalidOperationException("Journal is complete.");
                if(cost <= CapacityBytes-_retained) break;
                Monitor.Wait(_gate);
            }
            long sequence=checked(_sequence+1);
            var item=new JournalEvent(sequence,kind,bytes.ToArray());
            _events.Add(sequence,item); _sequence=sequence; _retained+=cost;
            _peakRetained=Math.Max(_peakRetained,_retained);
            Signal(); return sequence;
        }
    }
    public async ValueTask<JournalEvent?> ReadNextAsync(long after,CancellationToken cancellation)
    {
        if(after < 0) throw new ArgumentOutOfRangeException(nameof(after));
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();
            Task changed;
            lock(_gate)
            {
                ThrowIfDisposed();
                if(after < _checkpoint.Sequence) throw new InvalidOperationException("Journal prefix was checkpointed; restart from its checkpoint.");
                if(after > _sequence) throw new InvalidDataException("Reader is ahead of the journal.");
                if(after < _sequence) return _events[after+1];
                if(_completed) return null;
                changed=_changed.Task;
            }
            await changed.WaitAsync(cancellation).ConfigureAwait(false);
        }
    }
    public bool TryReadNext(long after,out JournalEvent? item)
    {
        lock(_gate)
        {
            ThrowIfDisposed();
            if(after < _checkpoint.Sequence || after > _sequence) throw new InvalidDataException("Invalid journal cursor.");
            if(after == _sequence) { item=null; return false; }
            item=_events[after+1]; return true;
        }
    }
    /// <summary>Persist must store an immutable candidate, not overwrite the active
    /// checkpoint. Only successful persistence publishes it and releases the prefix.</summary>
    public async Task PublishCheckpointAsync(string incarnation,long sequence,ReadOnlyMemory<byte> snapshot,
        Func<JournalCheckpoint,CancellationToken,Task> persist,CancellationToken cancellation=default)
    {
        ArgumentNullException.ThrowIfNull(persist);
        if(snapshot.Length is < 1 or > MaximumCheckpointBytes) throw new ArgumentOutOfRangeException(nameof(snapshot));
        await _checkpointGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            lock(_gate) ValidateCheckpoint(incarnation,sequence);
            var candidate=new JournalCheckpoint(sequence,snapshot.ToArray());
            await persist(candidate,cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            CommitStoredCheckpoint(incarnation,candidate);
        }
        finally { _checkpointGate.Release(); }
    }
    /// <summary>The caller transfers exclusively owned, fully stored immutable bytes.
    /// Used by the broker's in-memory store after upload and input-cut validation.</summary>
    internal void CommitStoredCheckpoint(string incarnation,JournalCheckpoint candidate)
    {
        if(candidate.Snapshot.Length is < 1 or > MaximumCheckpointBytes) throw new InvalidDataException("Invalid checkpoint storage.");
        lock(_gate)
        {
            ValidateCheckpoint(incarnation,candidate.Sequence);
            _checkpoint=candidate;
            while(_firstSequence <= candidate.Sequence)
            {
                var first=_events[_firstSequence];
                _events.Remove(_firstSequence++);
                _retained-=first.RetainedBytes;
            }
            Signal(); Monitor.PulseAll(_gate);
        }
    }
    private void ValidateCheckpoint(string incarnation,long sequence)
    {
        ThrowIfDisposed();
        if(incarnation != Incarnation || sequence < _checkpoint.Sequence || sequence < 0 || sequence > _sequence)
            throw new InvalidDataException("Invalid checkpoint incarnation or position.");
    }
    public void Complete()
    { lock(_gate) { ThrowIfDisposed(); _completed=true; Signal(); Monitor.PulseAll(_gate); } }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Signal() { var previous=_changed; _changed=NewSignal(); previous.TrySetResult(); }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed,this);
    public void Dispose()
    {
        lock(_gate)
        {
            if(_disposed) return;
            _disposed=true; _events.Clear(); _retained=0; _checkpoint=new(0,ReadOnlyMemory<byte>.Empty);
            Signal(); Monitor.PulseAll(_gate);
        }
        // Do not dispose the semaphore while an asynchronous persistence callback
        // owns it. It contains no OS handle unless somebody explicitly requests one.
    }
}
