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
    private static long _controlImageGeneration;
    private Dictionary<KittyImage,GhosttyImage> _controlImages = new(ReferenceEqualityComparer.Instance);

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
            (KittyImage Image, ImagePlacement Placement)[] controlImages;
            TerminalCommandMark[] marks;
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
                    (_core.MouseReporting ? 256u : 0) |
                    (_core.DynamicBg != 0 ? TerminalFrameState.DynamicBackgroundMode : 0);
                generation = _core.Generation;
                marks=_core.Marks.TakeLast(4096).Select(m=>new TerminalCommandMark(m.PromptLine,m.CommandLine,m.OutputLine,m.EndLine,m.ExitCode)).ToArray();
                state = new(_core.Cols, _core.Rows, _core.HistoryCount, scrollOffset, _core.ScrollGeneration,
                    0, 0, false, 0, _core.IsAltScreen, false, default, default,
                    _core.Title, _core.Cwd, modes, (uint)_core.KeyboardFlags, _core.ScrollTop, _core.ScrollBottom);
                _core.BeginPaint(scrollOffset);
                controlImages = scrollOffset == 0 ? _core.Placements
                    .Where(p=>_core.Images.ContainsKey(p.ImageId)).Select(p=>(_core.Images[p.ImageId],p)).ToArray() : [];
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
            var frame = new TerminalFrame(new(_incarnation, _epoch, viewportRevision, ++_sequence, generation), state, _scratch, pool:_pool,
                images:MergeControlImages(_core.PaintImages,controlImages,cellWidth,cellHeight),
                animationDeadline:_core.NextAnimationDeadline,wrapLengths:screen.WrapLengths,marks:marks);
            if (!_cache.Publish(frame)) { frame.Dispose(); throw new InvalidOperationException("Viewport revision moved backwards."); }
            return _cache.Acquire();
        }
    }

    public void Dispose()
    {
        lock (_captureLock) { _disposed = true; _cache.Dispose(); _scratch = []; _pool = null; _controlImages.Clear(); }
    }
    public TerminalFrame.Lease? AcquireCached() => _cache.Acquire();

    private IReadOnlyList<GhosttyImagePlacement> MergeControlImages(IReadOnlyList<GhosttyImagePlacement> native,
        (KittyImage Image,ImagePlacement Placement)[] controls,int cellWidth,int cellHeight)
    {
        var active=new Dictionary<KittyImage,GhosttyImage>(ReferenceEqualityComparer.Instance);
        if(controls.Length == 0) { _controlImages=active; return native; }
        var result=new List<GhosttyImagePlacement>(native);
        foreach(var (source,p) in controls)
        {
            if(!active.TryGetValue(source,out var image))
            {
                if(!_controlImages.TryGetValue(source,out image))
                {
                    var format=source.Format switch { KittyFormat.Rgb=>GhosttyPixelFormat.Rgb,KittyFormat.Rgba=>GhosttyPixelFormat.Rgba,
                        KittyFormat.Bgra=>GhosttyPixelFormat.Bgra,KittyFormat.Png=>GhosttyPixelFormat.Png,
                        _=>throw new InvalidDataException("Unsupported control image.") };
                    image=new((1UL<<63)|(ulong)Interlocked.Increment(ref _controlImageGeneration),source.Width,source.Height,format,source.Data);
                }
                active.Add(source,image);
            }
            int x=Math.Clamp(p.SrcX,0,source.Width-1),y=Math.Clamp(p.SrcY,0,source.Height-1);
            int w=p.SrcW > 0 ? Math.Min(p.SrcW,source.Width-x) : source.Width-x;
            int h=p.SrcH > 0 ? Math.Min(p.SrcH,source.Height-y) : source.Height-y;
            long width=p.Cols > 0 ? (long)p.Cols*cellWidth : w,height=p.Rows > 0 ? (long)p.Rows*cellHeight : h;
            if(width > 1_000_000 || height > 1_000_000) continue;
            result.Add(new(image,(uint)p.ImageId,GhosttyImageLayer.AboveText,0,p.Col,p.Row,0,0,
                (uint)width,(uint)height,(uint)x,(uint)y,(uint)w,(uint)h));
        }
        _controlImages=active;
        return result;
    }
}
