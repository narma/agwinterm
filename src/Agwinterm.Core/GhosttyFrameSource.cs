namespace Agwinterm.Core;

/// <summary>
/// Single capture consumer for a live owner. Native capture begins under the owner's
/// mutation lock; decoding and the immutable viewport copy happen outside it. The
/// capture gate serializes consumers, never output feeds. This source owns no terminal.
/// </summary>
public sealed class GhosttyFrameSource : IDisposable
{
    private readonly GhosttyTerminalCore _core;
    private readonly object _ownerLock, _captureLock;
    private readonly string _incarnation;
    private readonly ulong _epoch;
    private readonly TerminalFrameCache _cache;
    private Cell[] _scratch = [];
    private System.Buffers.ArrayPool<Cell>? _pool = System.Buffers.ArrayPool<Cell>.Create(TerminalFrame.MaximumCells,2);
    private ulong _sequence;
    private bool _disposed;

    public GhosttyFrameSource(GhosttyTerminalCore core, object ownerLock, string incarnation, ulong epoch, object? captureLock = null)
    {
        _core = core; _ownerLock = ownerLock; _incarnation = incarnation; _epoch = epoch; _captureLock = captureLock ?? new();
        _cache = new(incarnation, epoch);
    }

    public TerminalFrame.Lease? Capture(int scrollOffset, int cellWidth, int cellHeight, ulong viewportRevision = 0)
    {
        if (cellWidth <= 0 || cellHeight <= 0) throw new ArgumentOutOfRangeException(nameof(cellWidth));
        lock (_captureLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            TerminalFrameState state;
            long generation;
            lock (_ownerLock)
            {
                // Do not pair current metadata with an older synchronized-output viewport.
                if (_core.SynchronizedOutput) return _cache.Acquire();
                _core.CellPixelWidth = cellWidth; _core.CellPixelHeight = cellHeight;
                scrollOffset = Math.Clamp(scrollOffset, 0, _core.HistoryCount);
                uint modes = (_core.MouseClick ? 1u : 0) | (_core.MouseDrag ? 2u : 0) |
                    (_core.MouseMotion ? 4u : 0) | (_core.MouseSgr ? 8u : 0) |
                    (_core.MouseSgrPixels ? 16u : 0) | (_core.BracketedPaste ? 32u : 0) |
                    (_core.FocusReporting ? 64u : 0) | (_core.Win32InputMode ? 128u : 0) |
                    (_core.MouseReporting ? 256u : 0);
                generation = _core.Generation;
                state = new(_core.Cols, _core.Rows, _core.HistoryCount, scrollOffset, _core.ScrollGeneration,
                    0, 0, false, 0, _core.IsAltScreen, false, default, default,
                    _core.Title, _core.Cwd, modes, (uint)_core.KeyboardFlags, _core.ScrollTop, _core.ScrollBottom);
                _core.BeginPaint(scrollOffset);
            }
            var screen = _core.CompletePaint();
            var cursor = _core.PaintCursor;
            state = state with { Columns = screen.Cols, Rows = screen.Rows,
                CursorColumn = Math.Clamp(cursor.Col, 0, screen.Cols-1), CursorRow = Math.Clamp(cursor.Row, 0, screen.Rows-1),
                CursorVisible = cursor.Visible, CursorShape = _core.PaintCursorShape,
                Background = _core.PaintBackground, CursorColor = _core.PaintCursorColor };
            int length = checked(screen.Cols * screen.Rows);
            if (_scratch.Length != length) _scratch = new Cell[length];
            screen.CopyCellsTo(_scratch);
            var frame = new TerminalFrame(new(_incarnation, _epoch, viewportRevision, ++_sequence, generation), state, _scratch, pool:_pool);
            if (!_cache.Publish(frame)) { frame.Dispose(); throw new InvalidOperationException("Viewport revision moved backwards."); }
            return _cache.Acquire();
        }
    }

    public void Dispose()
    {
        lock (_captureLock) { _disposed = true; _cache.Dispose(); _scratch = []; _pool = null; }
    }
    public TerminalFrame.Lease? AcquireCached() => _cache.Acquire();
}
