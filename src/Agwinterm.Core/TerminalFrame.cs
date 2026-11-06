using System.Buffers;

namespace Agwinterm.Core;

public readonly record struct TerminalFrameIdentity(string Incarnation, ulong AttachmentEpoch,
    ulong ViewportRevision, ulong Sequence, long TerminalGeneration);
public sealed record TerminalCommandMark(int PromptLine,int CommandLine,int OutputLine,int EndLine,int? ExitCode);

public sealed record TerminalFrameState(int Columns, int Rows, int HistoryRows, int ScrollOffset,
    long ScrollGeneration, int CursorColumn, int CursorRow, bool CursorVisible, int CursorShape,
    bool AlternateScreen, bool SynchronizedOutput, Color Background, Color CursorColor,
    string Title, string WorkingDirectory, uint Modes = 0, uint KeyboardFlags = 0,
    int ScrollTop = 0, int ScrollBottom = 0)
{
    // The resolved frame background also has a color when the application
    // never issued OSC 11. Only a real override takes precedence over pane styling.
    public const uint DynamicBackgroundMode = 1u << 9;
    public const uint KnownModes = (DynamicBackgroundMode << 1) - 1;
}

/// <summary>
/// Immutable viewport storage shared by publisher/cache and read leases. Creation copies only
/// the bounded viewport into pooled storage. Dispose releases the owner's reference; outstanding
/// leases keep their exact generation alive. No native pointer or full history is retained here.
/// </summary>
public sealed class TerminalFrame : IDisposable
{
    public const int MaximumCells = 4_000_000;
    private Cell[]? _cells;
    private readonly ArrayPool<Cell> _pool;
    private readonly int _length;
    private int _references = 1, _ownerReleased;
    public TerminalFrameIdentity Identity { get; }
    public TerminalFrameState State { get; }
    public long CapturedTicks { get; }
    public IReadOnlyList<GhosttyImagePlacement> Images { get; }
    public long AnimationDeadline { get; }
    public IReadOnlyList<TerminalCommandMark> Marks { get; }
    private readonly int[]? _wrapLengths;

    public TerminalFrame(TerminalFrameIdentity identity, TerminalFrameState state, ReadOnlySpan<Cell> cells, long? capturedTicks = null, ArrayPool<Cell>? pool = null,
        IReadOnlyList<GhosttyImagePlacement>? images = null, long animationDeadline = 0, IReadOnlyList<int>? wrapLengths = null,
        IReadOnlyList<TerminalCommandMark>? marks = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrEmpty(identity.Incarnation) || identity.Incarnation.Length > 128 || identity.Sequence == 0 ||
            identity.AttachmentEpoch == 0 || identity.TerminalGeneration < 0)
            throw new ArgumentException("Invalid frame identity.", nameof(identity));
        if (state.Columns is < 1 or > 32768 || state.Rows is < 1 or > 32768 ||
            (long)state.Columns * state.Rows > MaximumCells || state.HistoryRows < 0 ||
            state.ScrollOffset < 0 || state.ScrollOffset > state.HistoryRows ||
            state.CursorColumn < 0 || state.CursorColumn >= state.Columns || state.CursorRow < 0 || state.CursorRow >= state.Rows)
            throw new ArgumentException("Invalid viewport bounds.", nameof(state));
        _length = checked(state.Columns * state.Rows);
        if (cells.Length != _length) throw new ArgumentException("Viewport cell count does not match dimensions.", nameof(cells));
        if (wrapLengths is not null && (wrapLengths.Count != state.Rows || wrapLengths.Any(w=>w < -1 || w > state.Columns)))
            throw new ArgumentException("Invalid frame wrap metadata.",nameof(wrapLengths));
        _wrapLengths=wrapLengths?.ToArray();
        Identity = identity; State = state;
        Images = images is { Count: > 0 } ? Array.AsReadOnly(images.ToArray()) : Array.Empty<GhosttyImagePlacement>();
        AnimationDeadline = animationDeadline;
        if(marks is { Count:>4096 }) throw new ArgumentException("Too many command marks.",nameof(marks));
        Marks=marks is null ? [] : Array.AsReadOnly(marks.ToArray());
        _pool = pool ?? ArrayPool<Cell>.Shared;
        _cells = _pool.Rent(_length);
        cells.CopyTo(_cells);
        CapturedTicks = capturedTicks ?? (TerminalLatencyTrace.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0);
    }

    public Lease Acquire()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _ownerReleased) != 0, this);
        return Retain();
    }
    private Lease Retain()
    {
        int count = Volatile.Read(ref _references);
        while (count > 0)
        {
            int observed = Interlocked.CompareExchange(ref _references, checked(count + 1), count);
            if (observed == count) return new(this);
            count = observed;
        }
        throw new ObjectDisposedException(nameof(TerminalFrame));
    }
    private void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            var cells = Interlocked.Exchange(ref _cells, null)!;
            // Pooled references must not keep prior terminal strings/links alive indefinitely.
            _pool.Return(cells, clearArray: true);
        }
    }
    public void Dispose() { if (Interlocked.Exchange(ref _ownerReleased, 1) == 0) Release(); }

    public sealed class Lease : IDisposable
    {
        private TerminalFrame? _frame;
        internal Lease(TerminalFrame frame) => _frame = frame;
        private TerminalFrame Frame => _frame ?? throw new ObjectDisposedException(nameof(Lease));
        public TerminalFrameIdentity Identity => Frame.Identity;
        public TerminalFrameState State => Frame.State;
        public long CapturedTicks => Frame.CapturedTicks;
        public IReadOnlyList<GhosttyImagePlacement> Images => Frame.Images;
        public long AnimationDeadline => Frame.AnimationDeadline;
        public IReadOnlyList<TerminalCommandMark> Marks => Frame.Marks;
        public int WrapLength(int row) => (uint)row < Frame.State.Rows ? Frame._wrapLengths?[row] ?? -1 : throw new ArgumentOutOfRangeException(nameof(row));
        /// <summary>Retain this exact generation, including after its publisher replaces it.</summary>
        public Lease Retain() => Frame.Retain();
        public Cell this[int row, int column]
        {
            get
            {
                var frame = Frame;
                if ((uint)row >= frame.State.Rows || (uint)column >= frame.State.Columns) throw new ArgumentOutOfRangeException(nameof(row));
                return frame._cells![row * frame.State.Columns + column];
            }
        }
        public void CopyCells(Span<Cell> destination)
        {
            var frame = Frame;
            frame._cells.AsSpan(0, frame._length).CopyTo(destination);
        }
        public void Dispose() { Interlocked.Exchange(ref _frame, null)?.Release(); }
    }
}

/// <summary>One published viewport. Late attachments, viewport generations and sequences are
/// rejected atomically. Callers own rejected frames and must dispose them as well.</summary>
public sealed class TerminalFrameCache : IDisposable
{
    private readonly object _gate = new();
    private string _incarnation;
    private ulong _epoch, _viewport;
    private TerminalFrame? _current;
    private bool _disposed;
    private static void ValidateAttachment(string incarnation, ulong epoch)
    {
        if (string.IsNullOrEmpty(incarnation) || incarnation.Length > 128 || epoch == 0)
            throw new ArgumentException("Invalid attachment identity.");
    }
    public TerminalFrameCache(string incarnation, ulong epoch)
    { ValidateAttachment(incarnation, epoch); _incarnation = incarnation; _epoch = epoch; }
    public void Reset(string incarnation, ulong epoch, ulong viewport = 0)
    {
        ValidateAttachment(incarnation, epoch);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (incarnation == _incarnation && epoch <= _epoch) throw new ArgumentException("Attachment epoch must advance.");
            _current?.Dispose(); _current = null;
            _incarnation = incarnation; _epoch = epoch; _viewport = viewport;
        }
    }
    public bool Publish(TerminalFrame frame)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var id = frame.Identity;
            if (id.Incarnation != _incarnation || id.AttachmentEpoch != _epoch || id.ViewportRevision < _viewport ||
                (_current is not null && id.Sequence <= _current.Identity.Sequence)) return false;
            var previous = _current; _current = frame; _viewport = id.ViewportRevision;
            previous?.Dispose(); return true;
        }
    }
    public TerminalFrame.Lease? Acquire() { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return _current?.Acquire(); } }
    public void Dispose() { lock (_gate) { _disposed = true; _current?.Dispose(); _current = null; } }
}
