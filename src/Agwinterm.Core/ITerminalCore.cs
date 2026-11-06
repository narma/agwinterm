namespace Agwinterm.Core;

/// <summary>The terminal state and operations consumed by a pane.</summary>
public interface ITerminalCore
{
    // ---- content ----
    ScreenBuffer Screen { get; }
    void Feed(ReadOnlySpan<byte> bytes);
    void Resize(int cols, int rows);

    // ---- cursor / screens ----
    int CursorRow { get; }
    int CursorCol { get; }
    bool CursorVisible { get; }
    bool IsAltScreen { get; }
    int ScrollTop { get; }
    int ScrollBottom { get; }

    // ---- modes ----
    bool MouseReporting { get; }
    bool MouseReportsMotion { get; }
    bool MouseSgr { get; }
    bool MouseSgrPixels { get; }
    bool MouseClick { get; }
    bool MouseDrag { get; }
    bool MouseMotion { get; }
    bool BracketedPaste { get; }
    bool FocusReporting { get; }
    bool SynchronizedOutput { get; }
    bool Win32InputMode { get; }
    uint DynamicBg { get; }
    int CursorShape { get; }
    int KeyboardFlags { get; }

    // ---- scrollback ----
    int ScrollbackMax { get; set; }
    int HistoryCount { get; }
    int GetHistoryRowLength(int row) => Screen.Cols;
    void RestoreHistory(Persist.PBuffer buffer) => throw new NotSupportedException();
    long ScrollGeneration { get; }
    Cell GetHistoryCell(int historyRow, int col);
    /// <summary>Automatic-wrap content length for a history + screen absolute row; -1 for a hard end.</summary>
    int GetRowWrapLength(int line);
    void SeedScrollback(IReadOnlyList<string> lines);

    // ---- marks / identity ----
    IReadOnlyList<ShellMark> Marks { get; }
    string Title { get; }
    string Cwd { get; }

    // ---- host actions ----
    IHostActions? Host { get; set; }

    // ---- images ----
    IReadOnlyDictionary<int, KittyImage> Images { get; }
    IReadOnlyList<ImagePlacement> Placements { get; }
    void ClearPlacements();
    bool HasImage(int id);
    void SetImageData(int id, KittyFormat format, int width, int height, byte[] data);
    void PlaceImage(int id, int row, int col, int cols, int rows,
        int srcX = 0, int srcY = 0, int srcW = 0, int srcH = 0);
    bool PlaceSixel(byte[] data);
    int CellPixelWidth { get; set; }
    int CellPixelHeight { get; set; }

    // ---- dumps (persistence / reattach) ----
    string DumpRow(int row);
    string DumpHistoryRow(int index);
    IReadOnlyList<string> DumpBuffer();
    string DumpModes();
    string DumpScreen() => throw new NotSupportedException();
}
