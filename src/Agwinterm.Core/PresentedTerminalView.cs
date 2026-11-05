using System.Text;

namespace Agwinterm.Core;

/// <summary>Transitional cached read surface for presentation consumers. Contains only
/// the bounded viewport, never a parser, native terminal, or history replica. Callers
/// serialize publication/reads with the session lock; retained frame leases are immutable.</summary>
public sealed class PresentedTerminalView : ITerminalCore, IDisposable
{
    private TerminalFrame.Lease? _frame;
    private ScreenBuffer _screen;
    private bool _dirty;
    private TerminalFrameState _empty;
    private TerminalFrameState State => _frame?.State ?? _empty;
    public PresentedTerminalView(int cols,int rows)
    { _screen=new(cols,rows); _empty=new(cols,rows,0,0,0,0,0,false,0,false,false,default,default,"",""); }
    public long AppliedTicks { get; private set; }
    public int Columns=>State.Columns;
    public int Rows=>State.Rows;
    public TerminalFrame.Lease? AcquireFrame() => _frame?.Retain();
    public void Publish(TerminalFrame frame)
    {
        if (_frame is not null && (frame.Identity.Incarnation != _frame.Identity.Incarnation ||
            frame.Identity.AttachmentEpoch != _frame.Identity.AttachmentEpoch || frame.Identity.Sequence <= _frame.Identity.Sequence))
            throw new InvalidDataException("Stale presentation frame.");
        var next=frame.Acquire(); var old=_frame; _frame=next; old?.Dispose(); _dirty=true;
        AppliedTicks=TerminalLatencyTrace.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
    }
    public ScreenBuffer Screen
    {
        get
        {
            if (!_dirty || _frame is null) return _screen;
            var state=_frame.State;
            if (_screen.Cols != state.Columns || _screen.Rows != state.Rows) _screen=new(state.Columns,state.Rows);
            for (int row=0;row<state.Rows;row++) for(int col=0;col<state.Columns;col++) _screen[row,col]=_frame[row,col];
            _dirty=false; return _screen;
        }
    }
    private static NotSupportedException OwnerOperation() => new("This operation must be sent to the terminal owner.");
    public void ShowError(string message)
    {
        _frame?.Dispose(); _frame=null; _dirty=false; _screen.Clear();
        int row=0,col=0;
        foreach(var rune in message.EnumerateRunes())
        {
            if(rune.Value == '\n') { row++; col=0; }
            else if(!Rune.IsControl(rune))
            {
                if(col == _screen.Cols) { row++; col=0; }
                if(row >= _screen.Rows) break;
                _screen[row,col++]=Cell.Empty with { Rune=rune.Value,Foreground=new(255,100,100) };
            }
            if(row >= _screen.Rows) break;
        }
    }
    public void Feed(ReadOnlySpan<byte> bytes) => throw OwnerOperation();
    public void Resize(int cols,int rows) { if (_frame is null) { _screen=new(cols,rows); _empty=_empty with { Columns=cols,Rows=rows }; } }
    public int CursorRow=>State.CursorRow;
    public int CursorCol=>State.CursorColumn;
    public bool CursorVisible=>State.CursorVisible;
    public int CursorShape=>State.CursorShape;
    public Color CursorColor=>State.CursorColor;
    public bool IsAltScreen=>State.AlternateScreen;
    public int ScrollTop=>State.ScrollTop;
    public int ScrollBottom=>State.ScrollBottom;
    private bool Mode(int bit)=>(State.Modes & (1u<<bit)) != 0;
    public bool MouseClick=>Mode(0);
    public bool MouseDrag=>Mode(1);
    public bool MouseMotion=>Mode(2);
    public bool MouseSgr=>Mode(3);
    public bool MouseSgrPixels=>Mode(4);
    public bool MouseReporting=>Mode(8);
    public bool MouseReportsMotion=>MouseDrag || MouseMotion;
    public bool BracketedPaste=>Mode(5);
    public bool FocusReporting=>Mode(6);
    public bool Win32InputMode=>Mode(7);
    public bool SynchronizedOutput=>State.SynchronizedOutput;
    public uint DynamicBg=>0xff000000u | (uint)(State.Background.R<<16 | State.Background.G<<8 | State.Background.B);
    public int KeyboardFlags=>(int)State.KeyboardFlags;
    public int ScrollbackMax { get; set; }=5000;
    public int HistoryCount=>State.HistoryRows;
    public long ScrollGeneration=>State.ScrollGeneration;
    public Cell GetHistoryCell(int row,int col)=>throw OwnerOperation();
    public int GetRowWrapLength(int line)=>throw OwnerOperation();
    public void SeedScrollback(IReadOnlyList<string> lines) { if(lines.Count != 0) throw OwnerOperation(); }
    public IReadOnlyList<TerminalEmulator.ShellMark> Marks=>[];
    public string Title=>State.Title;
    public string Cwd=>State.WorkingDirectory;
    public IHostActions? Host { get; set; }
    private static readonly IReadOnlyDictionary<int,KittyImage> EmptyImages=new Dictionary<int,KittyImage>();
    public IReadOnlyDictionary<int,KittyImage> Images=>EmptyImages;
    public IReadOnlyList<ImagePlacement> Placements=>[];
    public void ClearPlacements()=>throw OwnerOperation();
    public bool HasImage(int id)=>false;
    public void SetImageData(int id,KittyFormat format,int width,int height,byte[] data)=>throw OwnerOperation();
    public void PlaceImage(int id,int row,int col,int cols,int rows,int srcX=0,int srcY=0,int srcW=0,int srcH=0)=>throw OwnerOperation();
    public bool PlaceSixel(byte[] data)=>throw OwnerOperation();
    public int CellPixelWidth { get; set; }=8;
    public int CellPixelHeight { get; set; }=18;
    public string DumpRow(int row)
    {
        var screen=Screen; if((uint)row >= screen.Rows) return "";
        var text=new StringBuilder(); for(int col=0;col<screen.Cols;col++) text.Append(screen[row,col].Text);
        return text.ToString().TrimEnd();
    }
    public string DumpHistoryRow(int index)=>throw OwnerOperation();
    public IReadOnlyList<string> DumpBuffer()=>Enumerable.Range(0,Screen.Rows).Select(DumpRow).ToArray();
    public string DumpModes()=>throw OwnerOperation();
    public void Dispose() { _frame?.Dispose(); _frame=null; }
}
