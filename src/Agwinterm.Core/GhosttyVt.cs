using System.Text;
using System.Security.Cryptography;

namespace Agwinterm.Core;

/// <summary>
/// Owns Ghostty state and a reusable frame. Feed never copies a grid. BeginCapture
/// needs the session lock; CompleteCapture uses render-state-owned memory only.
/// A caller must serialize captures/disposal and keep the frame alive through paint.
/// </summary>
public sealed unsafe class GhosttyVt : IDisposable
{
    private readonly GhosttyNative.Handle _handle;
    private readonly GhosttyNative.Events _events = new();
    private GhosttyNative.NativeCell[] _cells;
    private byte[] _dirty;
    private int[] _wraps;
    private byte[] _text = new byte[4096];
    private byte[] _readText = new byte[4096];
    private GhosttyNative.NativeCell[] _readCells = [];
    private GhosttyNative.Image[] _imageMetadata = [];
    private readonly Dictionary<ulong, GhosttyImage> _imageCache = new();
    public IReadOnlyList<GhosttyImagePlacement> FrameImages { get; private set; } = [];
    public long NextAnimationDeadline { get; private set; }
    private readonly object _lifetime = new();
    private bool _capturing, _disposed;
    private volatile bool _disposeRequested;
    private GhosttyNative.Info _info;
    private Win32InputModeObserver _windows = new();
    public ScreenBuffer Frame { get; private set; }
    public int ChangedRows { get; private set; }
    public (int Col, int Row, bool Visible) FrameCursor { get; private set; }
    public int FrameCursorShape { get; private set; }
    public void SetCursorDefault(CursorStyle style, bool blink)
    {
        CheckAlive(); GhosttyNative.Check(GhosttyNative.CursorDefault(_handle, (uint)style, blink ? 1u : 0u));
    }
    public int GetFrameWrapLength(int row) => Frame.WrapLengths[row];
    public int CursorRow => (int)_info.CursorRow;
    public int CursorCol => (int)_info.CursorCol;
    public bool CursorVisible => _info.CursorVisible != 0;
    public bool IsAltScreen => _info.AltScreen != 0;
    public int HistoryCount => (int)_info.History;
    public int KeyboardFlags => (int)_info.KeyboardFlags;
    public uint Modes => _info.Modes;
    public uint Background => _info.Background;
    public uint DefaultBackground => _info.DefaultBackground;
    public long ScrollEpoch => unchecked((long)_info.ScrollEpoch);
    public GhosttyAnchor TrackPoint(int row, int col)
    {
        CheckAlive(); ArgumentOutOfRangeException.ThrowIfNegative(row); ArgumentOutOfRangeException.ThrowIfNegative(col);
        return new GhosttyAnchor(GhosttyNative.AnchorHandle.Create(_handle, (uint)col, (uint)row));
    }
    public bool Win32InputMode => _windows.Enabled;
    public IHostActions? Host { get; set; }
    private (Color Foreground, Color Background)? _hostColors;
    private void SyncHostColors()
    {
        var colors = Host?.DefaultColors;
        if (colors is null || colors == _hostColors) return;
        static uint Pack(Color color) => (uint)(color.R << 16 | color.G << 8 | color.B);
        GhosttyNative.Check(GhosttyNative.Colors(_handle, Pack(colors.Value.Foreground), Pack(colors.Value.Background)));
        _hostColors = colors; RefreshInfo();
    }
    public string Title { get; private set; } = "";
    public string Cwd { get; private set; } = "";
    private string _notificationTitle = "";
    private long _holdDeadline;
    public bool IsSynchronizedOutputHeld => Environment.TickCount64 < Volatile.Read(ref _holdDeadline);
    public IReadOnlyList<TerminalEmulator.ShellMark> ReadMarks()
    {
        CheckAlive();
        Span<GhosttyNative.Mark> marks = stackalloc GhosttyNative.Mark[512];
        nuint count;
        fixed (GhosttyNative.Mark* ptr = marks) GhosttyNative.Check(GhosttyNative.Marks(_handle, ptr, 512, out count));
        var result = new TerminalEmulator.ShellMark[(int)count];
        for (int i = 0; i < result.Length; i++)
            result[i] = new() { PromptLine = marks[i].Prompt, CommandLine = marks[i].Command,
                OutputLine = marks[i].Output, EndLine = marks[i].End, ExitCode = marks[i].HasExitCode != 0 ? marks[i].ExitCode : null };
        return result;
    }

    public static bool TryLoad(string directory, out string? error) => GhosttyNative.TryLoad(directory, out error);
    public GhosttyVt(int cols, int rows)
    {
        ValidateSize(cols, rows);
        _handle = GhosttyNative.Create(cols, rows, _events);
        try
        {
            Frame = new ScreenBuffer(cols, rows);
            _cells = new GhosttyNative.NativeCell[checked(cols * rows)];
            _dirty = new byte[rows]; _wraps = new int[rows];
            RefreshInfo();
        }
        catch { _handle.Dispose(); throw; }
    }
    private static void ValidateSize(int cols, int rows)
    {
        if (cols is < 1 or > 32768 || rows is < 1 or > 32768 || (long)cols * rows > 4_000_000)
            throw new ArgumentOutOfRangeException(nameof(cols), "Terminal dimensions exceed the configured limit.");
    }
    private void CheckAlive() => ObjectDisposedException.ThrowIf(_disposeRequested, this);
    private void RefreshInfo() => GhosttyNative.Check(GhosttyNative.GetInfo(_handle, out _info));
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        CheckAlive();
        SyncHostColors();
        fixed (byte* data = bytes) GhosttyNative.Check(GhosttyNative.Feed(_handle, data, (nuint)bytes.Length));
        _windows.Feed(bytes);
        RefreshInfo();
        DrainEvents();
        while (_windows.Completions.TryDequeue(out var completion)) Host?.CommandFinished(completion.Token, completion.Sequence);
    }
    private void DrainEvents()
    {
        if (_events.Error is { } error) throw new InvalidOperationException("Ghostty host effect failed.", error);
        while (_events.Pending.TryDequeue(out var effect))
        {
            _events.Bytes -= effect.Data.Length;
            switch (effect.Kind)
            {
                case 1: Host?.Respond(Encoding.UTF8.GetString(effect.Data)); break;
                case 2: Host?.Bell(); break;
                case 3: Title = ReadText(0); break;
                case 4: Cwd = ReadText(1); break;
                case 5: _notificationTitle = Encoding.UTF8.GetString(effect.Data); break;
                case 6: Host?.Notify(_notificationTitle, Encoding.UTF8.GetString(effect.Data)); break;
                case 7: Host?.Progress(BitConverter.ToInt32(effect.Data, 0), Math.Max(0, BitConverter.ToInt32(effect.Data, 4))); break;
                case 8:
                    string osc = Encoding.UTF8.GetString(effect.Data);
                    int delimiter = osc.IndexOf(';');
                    if (osc.StartsWith("777;", StringComparison.Ordinal) && delimiter >= 0 && ShellCompletion.TryParse(osc[(delimiter + 1)..], out string token, out long sequence))
                        Host?.CommandFinished(token, sequence);
                    else Host?.Unhandled("OSC", osc);
                    break;
                case 9: Host?.ClipboardWrite(Encoding.UTF8.GetString(effect.Data)); break;
                case 10: Volatile.Write(ref _holdDeadline, BitConverter.ToInt64(effect.Data)); break;
            }
        }
    }
    private string ReadText(uint kind)
    {
        while (true)
        {
            int result; nuint length;
            fixed (byte* text = _readText) result = GhosttyNative.Text(_handle, kind, text, (nuint)_readText.Length, out length);
            if (result == -3 && length <= 1024 * 1024) { Array.Resize(ref _readText, checked((int)length)); continue; }
            GhosttyNative.Check(result);
            return Encoding.UTF8.GetString(_readText.AsSpan(0, checked((int)length)));
        }
    }
    /// <summary>Explicit text/control read. Requires terminal synchronization, never called by Feed.</summary>
    public int ReadRow(int row, bool history, Span<Cell> destination)
    {
        CheckAlive(); ArgumentOutOfRangeException.ThrowIfNegative(row);
        SyncHostColors();
        int cols = (int)_info.Cols;
        if (destination.Length < cols) throw new ArgumentException("Row buffer is too small.", nameof(destination));
        if (_readCells.Length != cols) _readCells = new GhosttyNative.NativeCell[cols];
        int wrap;
        while (true)
        {
            int result;
            fixed (GhosttyNative.NativeCell* cells = _readCells)
            fixed (byte* text = _readText)
                result = GhosttyNative.ReadRow(_handle, (uint)row, history ? 1u : 0u, cells, (nuint)cols,
                    out wrap, text, (nuint)_readText.Length, out _);
            if (result == -3 && _readText.Length < 16 * 1024 * 1024) { Array.Resize(ref _readText, _readText.Length * 2); continue; }
            GhosttyNative.Check(result); break;
        }
        for (int col = 0; col < cols; col++) destination[col] = ConvertCell(_readCells[col], _readText);
        return wrap;
    }
    public void Resize(int cols, int rows, int cellWidth = 8, int cellHeight = 18)
    {
        CheckAlive(); ValidateSize(cols, rows);
        if (cellWidth < 1 || cellHeight < 1) throw new ArgumentOutOfRangeException(nameof(cellWidth));
        GhosttyNative.Check(GhosttyNative.Resize(_handle, (uint)cols, (uint)rows, (uint)cellWidth, (uint)cellHeight));
        RefreshInfo(); DrainEvents();
    }
    public void SetScrollbackLimit(int lines)
    {
        CheckAlive(); ArgumentOutOfRangeException.ThrowIfNegative(lines);
        GhosttyNative.Check(GhosttyNative.Scrollback(_handle, (nuint)lines)); RefreshInfo();
    }
    public byte[] CaptureSnapshot()
    {
        CheckAlive();
        if (_capturing) throw new InvalidOperationException("Finish capture before saving a snapshot.");
        GhosttyNative.Check(GhosttyNative.Snapshot(_handle, out nint pointer, out nuint length));
        try
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write("AGWVT001"u8); writer.Write(Encoding.ASCII.GetBytes(GhosttyNative.Revision));
            byte[] windows = _windows.Save(); writer.Write(windows.Length); writer.Write(checked((int)length));
            writer.Write(windows); writer.Write(new ReadOnlySpan<byte>((void*)pointer, checked((int)length)));
            writer.Flush(); byte[] payload = stream.ToArray();
            writer.Write(SHA256.HashData(payload)); writer.Flush();
            return stream.ToArray();
        }
        finally { GhosttyNative.SnapshotFree(_handle, pointer, length); }
    }
    public void RestoreSnapshot(ReadOnlySpan<byte> snapshot)
    {
        CheckAlive();
        if (_capturing) throw new InvalidOperationException("Finish capture before restoring a snapshot.");
        if (snapshot.Length is < 88 or > 65 * 1024 * 1024 || !snapshot[..8].SequenceEqual("AGWVT001"u8) ||
            Encoding.ASCII.GetString(snapshot.Slice(8, 40)) != GhosttyNative.Revision ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(snapshot[..^32]), snapshot[^32..]))
            throw new InvalidDataException("Invalid, damaged or incompatible Ghostty snapshot.");
        int windowsLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(snapshot.Slice(48, 4));
        int nativeLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(snapshot.Slice(52, 4));
        if (windowsLength != 397 || nativeLength < 1 || (long)windowsLength + nativeLength + 88 != snapshot.Length)
            throw new InvalidDataException("Invalid Ghostty snapshot lengths.");
        var windows = new Win32InputModeObserver(); windows.Restore(snapshot.Slice(56, windowsLength).ToArray());
        fixed (byte* data = snapshot.Slice(56 + windowsLength, nativeLength))
            GhosttyNative.Check(GhosttyNative.Restore(_handle, data, (nuint)nativeLength));
        _windows = windows; _hostColors = null; Volatile.Write(ref _holdDeadline, 0); RefreshInfo(); Title = ReadText(0); Cwd = ReadText(1);
    }
    public void BeginCapture()
    {
        CheckAlive();
        if (_capturing) throw new InvalidOperationException("A frame capture is already in progress.");
        SyncHostColors();
        // Allocate before begin so an allocation failure cannot strand an incomplete native update.
        int cols = (int)_info.Cols, rows = (int)_info.Rows;
        if (Frame.Cols != cols || Frame.Rows != rows)
        {
            var frame = new ScreenBuffer(cols, rows);
            var cells = new GhosttyNative.NativeCell[checked(cols * rows)];
            var dirty = new byte[rows]; var wraps = new int[rows];
            Frame = frame; _cells = cells; _dirty = dirty; _wraps = wraps;
        }
        GhosttyNative.Check(GhosttyNative.AnimationTick(_handle, out ulong animationDeadline));
        NextAnimationDeadline = checked((long)animationDeadline);
        GhosttyNative.Check(GhosttyNative.Begin(_handle));
        _capturing = true;
    }
    public void CompleteCapture()
    {
        // Dispose may run under the session lock while this snapshot is being
        // converted outside it. Finish the sole reader before releasing native state.
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_capturing) throw new InvalidOperationException("BeginCapture must precede CompleteCapture.");
        try
        {
            GhosttyNative.Check(GhosttyNative.End(_handle));
            GhosttyNative.Check(GhosttyNative.Cursor(_handle, out uint cursorCol, out uint cursorRow, out uint cursorVisible, out uint cursorShape));
            FrameCursor = ((int)cursorCol, (int)cursorRow, cursorVisible != 0);
            FrameCursorShape = (int)cursorShape;
            CaptureImages();
            while (true)
            {
                int result;
                fixed (GhosttyNative.NativeCell* cells = _cells)
                fixed (byte* dirty = _dirty, text = _text)
                fixed (int* wraps = _wraps)
                    result = GhosttyNative.Cells(_handle, cells, (nuint)_cells.Length, dirty, wraps,
                        (nuint)_dirty.Length, text, (nuint)_text.Length, out _);
                if (result == -3 && _text.Length < 16 * 1024 * 1024) { Array.Resize(ref _text, _text.Length * 2); continue; }
                GhosttyNative.Check(result);
                break;
            }
            ChangedRows = 0;
            for (int row = 0; row < Frame.Rows; row++)
            {
                if (_dirty[row] == 0) continue;
                ChangedRows++;
                for (int col = 0; col < Frame.Cols; col++)
                {
                    var cell = _cells[row * Frame.Cols + col];
                    Frame[row, col] = ConvertCell(cell, _text);
                }
                Frame.WrapLengths[row] = _wraps[row];
            }
            GhosttyNative.Check(GhosttyNative.Clean(_handle));
        }
        catch
        {
            // Preserve the original failure, while releasing native reader ownership.
            // The next capture is forced to refresh all rows.
            _ = GhosttyNative.Abort(_handle);
            throw;
        }
        finally
        {
            lock (_lifetime)
            {
                _capturing = false;
                if (_disposeRequested) ReleaseNative();
            }
        }
    }
    private void CaptureImages()
    {
        nuint count;
        while (true)
        {
            int result;
            fixed (GhosttyNative.Image* metadata = _imageMetadata)
                result = GhosttyNative.Images(_handle, metadata, (nuint)_imageMetadata.Length, out count, (uint)sizeof(GhosttyNative.Image));
            if (result == -3 && count <= 65536) { Array.Resize(ref _imageMetadata, checked((int)count)); continue; }
            GhosttyNative.Check(result); break;
        }
        if (count == 0) { FrameImages = []; _imageCache.Clear(); return; }
        var placements = new GhosttyImagePlacement[checked((int)count)];
        var retained = new HashSet<ulong>();
        for (int i = 0; i < placements.Length; i++)
        {
            var item = _imageMetadata[i]; retained.Add(item.Generation);
            if (!_imageCache.TryGetValue(item.Generation, out var image))
            {
                int channels = (GhosttyPixelFormat)item.Format switch
                {
                    GhosttyPixelFormat.Rgb => 3, GhosttyPixelFormat.Rgba => 4,
                    GhosttyPixelFormat.GrayAlpha => 2, GhosttyPixelFormat.Gray => 1,
                    _ => throw new InvalidDataException("Unexpected native image format.")
                };
                if (item.DataLength > 64 * 1024 * 1024 || (ulong)item.ImageWidth * item.ImageHeight * (uint)channels != item.DataLength)
                    throw new InvalidDataException("Invalid native image dimensions.");
                byte[] pixels = GC.AllocateUninitializedArray<byte>(checked((int)item.DataLength));
                fixed (byte* data = pixels)
                    GhosttyNative.Check(GhosttyNative.ImagePixels(_handle, item.Generation, data, (nuint)pixels.Length));
                image = new GhosttyImage(item.Generation, checked((int)item.ImageWidth), checked((int)item.ImageHeight),
                    (GhosttyPixelFormat)item.Format, pixels);
                _imageCache.Add(item.Generation, image);
            }
            placements[i] = new(image, item.Id, (GhosttyImageLayer)item.Layer, item.Z, item.Col, item.Row,
                item.OffsetX, item.OffsetY, item.Width, item.Height, item.SourceX, item.SourceY, item.SourceWidth, item.SourceHeight);
        }
        foreach (ulong key in _imageCache.Keys.ToArray()) if (!retained.Contains(key)) _imageCache.Remove(key);
        Array.Sort(placements, static (a, b) => a.Layer != b.Layer ? a.Layer.CompareTo(b.Layer) : a.Z.CompareTo(b.Z));
        FrameImages = placements;
    }
    private static Cell ConvertCell(GhosttyNative.NativeCell cell, byte[] text)
    {
        string? grapheme = cell.TextLength == 0 ? null : Encoding.UTF8.GetString(text.AsSpan(checked((int)cell.TextOffset), checked((int)cell.TextLength)));
        var link = cell.LinkLength is 0 or uint.MaxValue ? null : new TerminalHyperlink(
            Encoding.UTF8.GetString(text.AsSpan(checked((int)cell.LinkOffset), checked((int)cell.LinkLength))));
        return new Cell((int)cell.Rune, Unpack(cell.Fg), Unpack(cell.Bg), (CellAttributes)cell.Attributes,
            (byte)cell.Width, Spec(cell.FgKind, cell.FgValue), Spec(cell.BgKind, cell.BgValue), link, grapheme);
    }
    private static Color Unpack(uint color) => new((byte)(color >> 16), (byte)(color >> 8), (byte)color);
    private static ColorSpec Spec(uint kind, uint value) => new((ColorSpecKind)kind, (byte)value, Unpack(value));
    public void Dispose()
    {
        // Serialize with terminal mutations/begin-capture using the caller's
        // session lock. The internal gate only coordinates the external reader.
        lock (_lifetime)
        {
            _disposeRequested = true;
            if (!_capturing) ReleaseNative();
        }
    }
    private void ReleaseNative()
    {
        if (_disposed) return;
        _disposed = true; _handle.Dispose(); _imageCache.Clear(); FrameImages = [];
    }
}
