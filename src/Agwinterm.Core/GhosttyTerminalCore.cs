using System.Text;

namespace Agwinterm.Core;

/// <summary>
/// Ghostty is authoritative. The transitional ScreenBuffer is materialized only
/// on an explicit read, never after Feed; history is fetched one row at a time.
/// Mutations and explicit reads use the session lock. Paint completion uses
/// snapshot-owned storage outside that lock, with one UI capture consumer.
/// </summary>
public sealed class GhosttyTerminalCore : ITerminalCore, IDisposable
{
    private readonly GhosttyVt _vt;
    private int _cols, _rows, _scrollbackMax = 5000;
    private bool _screenDirty = true;
    private Cell[] _historyRow;
    private ScreenBuffer _queryScreen;
    private Cell[] _queryRow;
    private CursorStyle _cursorStyle;
    private bool _cursorBlink;
    private int _historyIndex = -1, _historyWrap;
    private IReadOnlyList<TerminalEmulator.ShellMark>? _marks;
    private readonly Dictionary<int, KittyImage> _hostImages = new();
    private readonly List<ImagePlacement> _hostPlacements = new();
    public GhosttyTerminalCore(int cols, int rows, CursorStyle cursorStyle = CursorStyle.Bar, bool cursorBlink = true)
    {
        _vt = new GhosttyVt(cols, rows); _cols = cols; _rows = rows;
        _vt.SetCursorDefault(cursorStyle, cursorBlink);
        _cursorStyle = cursorStyle; _cursorBlink = cursorBlink;
        _queryScreen = new ScreenBuffer(cols, rows); _queryRow = new Cell[cols];
        _historyRow = new Cell[cols]; _vt.SetScrollbackLimit(_scrollbackMax);
    }
    public static bool TryLoad(string directory, out string? error) => GhosttyVt.TryLoad(directory, out error);
    public ScreenBuffer Screen { get { EnsureScreen(); return _queryScreen; } }
    public int Cols => _cols;
    public int Rows => _rows;
    /// <summary>UI consumer only, under the session lock. Does not populate the query screen.</summary>
    private int _paintCellWidth = 8, _paintCellHeight = 18;
    public void BeginPaint(int scrollOffset = 0)
    {
        if (_paintCellWidth != CellPixelWidth || _paintCellHeight != CellPixelHeight)
        {
            _vt.Resize(_cols, _rows, CellPixelWidth, CellPixelHeight);
            _paintCellWidth = CellPixelWidth; _paintCellHeight = CellPixelHeight;
        }
        _vt.BeginCapture(scrollOffset);
    }
    /// <summary>UI consumer only, outside the session lock; queries use independent buffers.</summary>
    public ScreenBuffer CompletePaint() { _vt.CompleteCapture(); return _vt.Frame; }
    public (int Col, int Row, bool Visible) PaintCursor => _vt.FrameCursor;
    public int PaintCursorShape => _vt.FrameCursorShape;
    public Color PaintBackground => _vt.FrameBackground;
    public Color PaintCursorColor => _vt.FrameCursorColor;
    public IReadOnlyList<GhosttyImagePlacement> PaintImages => _vt.FrameImages;
    public long NextAnimationDeadline => _vt.NextAnimationDeadline;
    public byte[] CaptureSnapshot() => _vt.CaptureSnapshot();
    public void RestoreSnapshot(ReadOnlySpan<byte> snapshot)
    {
        _vt.RestoreSnapshot(snapshot);
        _cols = _vt.Columns; _rows = _vt.Rows; _historyRow = new Cell[_cols];
        _hostImages.Clear(); _hostPlacements.Clear(); Invalidate();
    }
    public byte[] EncodeKey(uint virtualKey, uint action, uint modifiers, string text = "", uint unshifted = 0, uint consumed = 0)
        => _vt.EncodeKey(virtualKey, action, modifiers, text, unshifted, consumed);
    public byte[] EncodeMouse(uint action, uint button, uint modifiers, float x, float y, uint cw, uint ch)
        => _vt.EncodeMouse(action, button, modifiers, x, y, cw, ch);
    public byte[] EncodePaste(string text) => _vt.EncodePaste(text);
    public IReadOnlyList<(int Line, int Col0, int EndLine, int Col1)> Search(string query) => _vt.Search(query);
    /// <summary>UI-thread-only read of the last captured paint frame, including a synchronized hold.</summary>
    public ScreenBuffer LastPaintFrame => _vt.Frame;
    public void SetCursorDefault(CursorStyle style, bool blink)
    {
        if (_cursorStyle == style && _cursorBlink == blink) return;
        _vt.SetCursorDefault(style, blink); _cursorStyle = style; _cursorBlink = blink;
    }
    private long _queryThemeVersion = -1;
    private void SyncQueryTheme()
    {
        _vt.SyncHostColors();
        if (_queryThemeVersion == _vt.ThemeVersion) return;
        _queryThemeVersion = _vt.ThemeVersion; _screenDirty = true; _historyIndex = -1;
    }
    private void EnsureScreen()
    {
        SyncQueryTheme();
        if (!_screenDirty) return;
        if (_queryScreen.Cols != _cols || _queryScreen.Rows != _rows)
        { _queryScreen = new ScreenBuffer(_cols, _rows); _queryRow = new Cell[_cols]; }
        for (int row = 0; row < _rows; row++)
        {
            int wrap = _vt.ReadRow(row, false, _queryRow);
            for (int col = 0; col < _cols; col++) _queryScreen[row, col] = _queryRow[col];
            _queryScreen.WrapLengths[row] = wrap;
        }
        _screenDirty = false;
    }
    internal long TextVersion { get; private set; }
    private void Invalidate() { _screenDirty = true; _historyIndex = -1; _marks = null; TextVersion++; }
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        _vt.Feed(bytes); Invalidate();
    }
    public void Resize(int cols, int rows)
    {
        _vt.Resize(cols, rows, CellPixelWidth, CellPixelHeight);
        _cols = cols; _rows = rows; _historyRow = new Cell[cols]; Invalidate();
    }
    public int CursorRow => _vt.CursorRow;
    public int CursorCol => _vt.CursorCol;
    public bool CursorVisible => _vt.CursorVisible;
    public bool IsAltScreen => _vt.IsAltScreen;
    public int ScrollTop => _vt.ScrollTop;
    public int ScrollBottom => _vt.ScrollBottom;
    private bool Mode(int bit) => (_vt.Modes & (1u << bit)) != 0;
    public bool MouseClick => Mode(0);
    public bool MouseDrag => Mode(1);
    public bool MouseMotion => Mode(2);
    public bool MouseSgr => Mode(3);
    public bool MouseSgrPixels => Mode(4);
    public bool MouseReporting => MouseClick || MouseDrag || MouseMotion || Mode(8);
    public bool MouseReportsMotion => MouseDrag || MouseMotion;
    public bool BracketedPaste => Mode(5);
    public bool FocusReporting => Mode(6);
    public bool SynchronizedOutput => _vt.IsSynchronizedOutputHeld;
    public bool Win32InputMode => _vt.Win32InputMode;
    public uint DynamicBg => _vt.HasBackgroundOverride ? 0xff000000u | _vt.Background : 0;
    public int CursorShape => _vt.CursorShape;
    public int KeyboardFlags => _vt.KeyboardFlags;
    public int ScrollbackMax
    {
        get => _scrollbackMax;
        set { _vt.SetScrollbackLimit(Math.Max(0, value)); _scrollbackMax = Math.Max(0, value); Invalidate(); }
    }
    public int HistoryCount => _vt.HistoryCount;
    // A change epoch for scroll-to-bottom invalidation. Selection uses native
    // tracked positions, so it does not infer eviction counts from this value.
    public long ScrollGeneration => _vt.ScrollEpoch;
    public GhosttyAnchor TrackPoint(int row, int col) => _vt.TrackPoint(row, col);
    public GhosttySelection CreateSelection(int ar, int ac, int fr, int fc, bool block)
        => new(this, ar, ac, fr, fc, block);
    private void ReadHistory(int row)
    {
        SyncQueryTheme();
        if (_historyIndex == row) return;
        _historyWrap = _vt.ReadRow(row, true, _historyRow); _historyIndex = row;
    }
    public Cell GetHistoryCell(int historyRow, int col)
    {
        if ((uint)historyRow >= HistoryCount || (uint)col >= _cols) return Cell.Empty;
        ReadHistory(historyRow); return _historyRow[col];
    }
    public int GetRowWrapLength(int line)
    {
        if (line < HistoryCount) { ReadHistory(line); return _historyWrap; }
        EnsureScreen(); return _queryScreen.WrapLengths[line - HistoryCount];
    }
    public void SeedScrollback(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;
        // This legacy plain-text import must never execute escape sequences from a saved file.
        var text = new StringBuilder();
        foreach (string line in lines)
        {
            foreach (var rune in line.EnumerateRunes()) text.Append(Rune.IsControl(rune) ? " " : rune.ToString());
            text.Append("\r\n");
        }
        _vt.PrependHistory(_cols, Encoding.UTF8.GetBytes(text.ToString()), _scrollbackMax);
        Invalidate();
    }
    public void RestoreHistory(Persist.PBuffer buffer)
    {
        byte[] encoded = GhosttyHistory.Encode(buffer);
        _vt.PrependHistory(checked((int)buffer.Cols), encoded, _scrollbackMax);
        Invalidate();
    }
    public IReadOnlyList<TerminalEmulator.ShellMark> Marks => _marks ??= _vt.ReadMarks();
    public string Title => _vt.Title;
    public string Cwd => _vt.Cwd;
    public IHostActions? Host { get => _vt.Host; set => _vt.Host = value; }
    public IReadOnlyDictionary<int, KittyImage> Images => _hostImages;
    public IReadOnlyList<ImagePlacement> Placements => _hostPlacements;
    public void ClearPlacements() => _hostPlacements.Clear();
    public bool HasImage(int id) => _hostImages.ContainsKey(id);
    public void SetImageData(int id, KittyFormat format, int width, int height, byte[] data)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 64 * 1024 * 1024 || data.Length > 256 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(width));
        _hostImages[id] = new KittyImage(id, format, width, height, data);
    }
    public void PlaceImage(int id, int row, int col, int cols, int rows, int srcX = 0, int srcY = 0, int srcW = 0, int srcH = 0)
    {
        if (!HasImage(id)) return;
        _hostPlacements.RemoveAll(p => p.ImageId == id);
        _hostPlacements.Add(new ImagePlacement(id, row, col, cols, rows, srcX, srcY, srcW, srcH));
    }
    public bool PlaceSixel(byte[] data) => false;
    public int CellPixelWidth { get; set; } = 8;
    public int CellPixelHeight { get; set; } = 18;
    public string DumpRow(int row)
    {
        EnsureScreen(); var text = new StringBuilder();
        for (int col = 0; col < _cols; col++) TerminalSelectionText.AppendCell(text, _queryScreen[row, col]);
        return text.ToString().TrimEnd();
    }
    public string DumpHistoryRow(int index)
    {
        ReadHistory(index); var text = new StringBuilder();
        foreach (var cell in _historyRow) TerminalSelectionText.AppendCell(text, cell);
        return text.ToString().TrimEnd();
    }
    public IReadOnlyList<string> DumpBuffer()
    {
        var result = new List<string>(HistoryCount + _rows);
        for (int row = 0; row < HistoryCount; row++) result.Add(DumpHistoryRow(row));
        for (int row = 0; row < _rows; row++) result.Add(DumpRow(row));
        return result;
    }
    public string DumpModes()
    {
        var text = new StringBuilder();
        foreach (var (mode, enabled) in new[] { (25, CursorVisible), (1000, MouseClick), (1002, MouseDrag), (1003, MouseMotion),
            (1006, MouseSgr), (1016, MouseSgrPixels), (2004, BracketedPaste), (1004, FocusReporting), (9001, Win32InputMode) })
            text.Append($"\u001b[?{mode}{(enabled ? 'h' : 'l')}");
        text.Append($"\u001b[={KeyboardFlags}u");
        return text.ToString();
    }
    public void Dispose() { _vt.Dispose(); _hostImages.Clear(); _hostPlacements.Clear(); }
}
