using Agwinterm.Core;

namespace Agwinterm.Pty;

/// <summary>Transport metadata only. No parser, viewport, history, native terminal
/// or reply generator exists in a journal broker. Legacy state reads fail explicitly.</summary>
internal sealed class JournalBrokerCore : ITerminalCore
{
    private static NotSupportedException NoTerminal() => new("Terminal state belongs to the journal owner.");
    public void Feed(ReadOnlySpan<byte> bytes) { }
    public void Resize(int cols,int rows) { }
    public ScreenBuffer Screen => throw NoTerminal();
    public int CursorRow => 0;
    public int CursorCol => 0;
    public bool CursorVisible => false;
    public bool IsAltScreen => false;
    public int ScrollTop => 0;
    public int ScrollBottom => 0;
    public bool MouseReporting => false;
    public bool MouseReportsMotion => false;
    public bool MouseSgr => false;
    public bool MouseSgrPixels => false;
    public bool MouseClick => false;
    public bool MouseDrag => false;
    public bool MouseMotion => false;
    public bool BracketedPaste => false;
    public bool FocusReporting => false;
    public bool SynchronizedOutput => false;
    public bool Win32InputMode => false;
    public uint DynamicBg => 0;
    public int CursorShape => 0;
    public int KeyboardFlags => 0;
    public int ScrollbackMax { get; set; }
    public int HistoryCount => 0;
    public long ScrollGeneration => 0;
    public Cell GetHistoryCell(int historyRow,int col) => throw NoTerminal();
    public int GetRowWrapLength(int line) => throw NoTerminal();
    public void SeedScrollback(IReadOnlyList<string> lines) => throw NoTerminal();
    public IReadOnlyList<TerminalEmulator.ShellMark> Marks => [];
    public string Title => "";
    public string Cwd => "";
    public IHostActions? Host { get; set; }
    public IReadOnlyDictionary<int,KittyImage> Images { get; } = new Dictionary<int,KittyImage>();
    public IReadOnlyList<ImagePlacement> Placements => [];
    public void ClearPlacements() => throw NoTerminal();
    public bool HasImage(int id) => false;
    public void SetImageData(int id,KittyFormat format,int width,int height,byte[] data) => throw NoTerminal();
    public void PlaceImage(int id,int row,int col,int cols,int rows,int srcX=0,int srcY=0,int srcW=0,int srcH=0) => throw NoTerminal();
    public bool PlaceSixel(byte[] data) => throw NoTerminal();
    public int CellPixelWidth { get; set; }
    public int CellPixelHeight { get; set; }
    public string DumpRow(int row) => throw NoTerminal();
    public string DumpHistoryRow(int index) => throw NoTerminal();
    public IReadOnlyList<string> DumpBuffer() => throw NoTerminal();
    public string DumpModes() => throw NoTerminal();
}
