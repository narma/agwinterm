using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Pty;

/// <summary>Direct in-process adapter. Owns frame leases and its ordered input
/// writer, but never constructs a terminal, opens a transport or owns the PTY.</summary>
public sealed class LocalSessionPresentation : ISessionPresentation, IDisposable
{
    private readonly TerminalSession _session;
    private readonly GhosttyTerminalCore _core;
    private readonly GhosttyFrameSource _frames;
    private readonly OwnerInputQueue _input;
    private readonly OwnerActions _actions;
    private readonly OwnerQueries _queries;
    private readonly object _captureGate=new();
    private readonly string _incarnation=Guid.NewGuid().ToString("N");
    private ulong _viewportRevision;
    private int _scrollOffset, _cellWidth=8, _cellHeight=18;
    private bool _visible=true;
    private volatile bool _disposed;
    public bool IsRemote => false;

    internal LocalSessionPresentation(TerminalSession session)
    {
        _session=session;
        _queries=new(session,_incarnation,1);
        _core=session.Emulator as GhosttyTerminalCore ?? throw new ArgumentException("Ghostty owner required.",nameof(session));
        _input=new(session);
        _actions=new(_input,session,_core.Host);
        _core.Host=_actions;
        _frames=new(_core,session.SyncRoot,_incarnation,1,_captureGate);
    }
    public void SetHost(IHostActions host)
    {
        lock(_session.SyncRoot) { ObjectDisposedException.ThrowIf(_disposed,this); _actions.Target=host; _core.SyncTheme(); }
    }
    public TerminalFrame.Lease? Capture(int scrollOffset,int cellWidth,int cellHeight)
    {
        if(scrollOffset < 0 || cellWidth <= 0 || cellHeight <= 0) throw new ArgumentOutOfRangeException(nameof(scrollOffset));
        lock(_captureGate)
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            if(!_visible) return null;
            if(_scrollOffset != scrollOffset || _cellWidth != cellWidth || _cellHeight != cellHeight)
            { _scrollOffset=scrollOffset; _cellWidth=cellWidth; _cellHeight=cellHeight; _viewportRevision++; }
            return _frames.Capture(scrollOffset,cellWidth,cellHeight,_viewportRevision);
        }
    }
    public TerminalInputAcceptance Submit(TerminalInput input)
        => _disposed ? TerminalInputAcceptance.Closed : _input.Submit(input);

    internal void Resize(int columns,int rows)
    {
        lock(_captureGate)
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            if(columns == _session.Cols && rows == _session.Rows) return;
            if(columns is < 1 or > 10000 || rows is < 1 or > 10000 || (long)columns*rows > TerminalFrame.MaximumCells)
                throw new ArgumentOutOfRangeException(nameof(columns));
            _viewportRevision++;
            _session.ResizeOwner(columns,rows);
        }
    }

    public TerminalInputAcceptance Execute(TerminalCommand command)
    {
        lock(_captureGate)
        {
            if(_disposed) return TerminalInputAcceptance.Closed;
            switch(command)
            {
                case TerminalCommand.Resize resize:
                    if(resize.Columns <= 0 || resize.Rows <= 0 || (long)resize.Columns*resize.Rows > TerminalFrame.MaximumCells ||
                        resize.CellWidth <= 0 || resize.CellHeight <= 0 || resize.ViewportRevision < _viewportRevision)
                        throw new ArgumentOutOfRangeException(nameof(command));
                    _viewportRevision=resize.ViewportRevision; _cellWidth=resize.CellWidth; _cellHeight=resize.CellHeight;
                    lock(_session.SyncRoot) { _core.CellPixelWidth=_cellWidth; _core.CellPixelHeight=_cellHeight; }
                    _session.ResizeOwner(resize.Columns,resize.Rows);
                    break;
                case TerminalCommand.Viewport viewport:
                    if(viewport.ScrollOffset < 0 || viewport.ViewportRevision < _viewportRevision) throw new ArgumentOutOfRangeException(nameof(command));
                    _viewportRevision=viewport.ViewportRevision; _scrollOffset=viewport.ScrollOffset; _visible=viewport.Visible;
                    break;
                case TerminalCommand.InjectOutput inject:
                    return _input.Submit(new TerminalInput.Command(inject));
                case TerminalCommand.Clear:
                case TerminalCommand.Reset:
                case TerminalCommand.ScrollbackLimit:
                    return _input.Submit(new TerminalInput.Command(command));
                case TerminalCommand.Theme theme:
                    var colors=ThemeColors.Parse(theme.Colors);
                    var palette=ThemeColors.ParsePalette(theme.Palette);
                    var cursor=ThemeColors.ParseColor(theme.Cursor);
                    if(colors is null || palette is null || cursor is null) throw new ArgumentException("Invalid terminal theme.",nameof(command));
                    _session.MutateLocked(_=>{ _actions.Colors=colors; _actions.Palette=palette; _actions.Cursor=cursor; _core.SyncTheme(); });
                    break;
                default: throw new ArgumentException("Unknown owner command.",nameof(command));
            }
            return TerminalInputAcceptance.Accepted;
        }
    }

    public ValueTask<TerminalQueryResult> QueryAsync(TerminalQuery query,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query.Operation is "history" or "search" or "buffer" or "styled-buffer" or "text-cells" ||
            query.Operation.StartsWith("selection-",StringComparison.Ordinal))
            return _queries.ReadAsync(query,cancellationToken);
        return new(Task.Run(()=>ReadViewport(query,cancellationToken),cancellationToken));
    }
    private TerminalQueryResult ReadViewport(TerminalQuery query,CancellationToken cancellation)
    {
        TerminalQueryResult Result(string status,IReadOnlyList<string>? rows=null,bool truncated=false)
            => new(_incarnation,1,query.Generation,status,rows ?? [],truncated);
        if(_disposed) return Result("closed");
        if(query.Incarnation != _incarnation || query.AttachmentEpoch != 1) return Result("stale");
        if(query.Operation != "viewport") return Result("unsupported"); // history/search use their dedicated owner captures
        if(query.StartRow is < 0 or > 32768 || query.RowCount is < 0 or > 256 || query.ResultLimit is < 1 or > 1_000_000) return Result("invalid");
        TerminalFrame.Lease? captured;
        try { captured=_frames.AcquireCached(); }
        catch(ObjectDisposedException) { return Result("closed"); }
        using var frame=captured;
        if(frame is null || frame.Identity.TerminalGeneration != query.Generation) return Result("stale");
        int end=Math.Min(frame.State.Rows,checked(query.StartRow+query.RowCount));
        var rows=new List<string>();
        int characters=0;
        for(int row=query.StartRow;row<end;row++)
        {
            cancellation.ThrowIfCancellationRequested();
            var text=new StringBuilder();
            for(int col=0;col<frame.State.Columns;col++)
            {
                var cell=frame[row,col];
                if(cell.Width == 0) continue;
                string value=cell.Grapheme ?? char.ConvertFromUtf32(cell.Rune);
                if(characters+value.Length > query.ResultLimit) return Result("ok",rows,true);
                text.Append(value); characters+=value.Length;
            }
            rows.Add(text.ToString());
        }
        cancellation.ThrowIfCancellationRequested();
        return _disposed ? Result("closed") : Result("ok",rows,end < query.StartRow+query.RowCount);
    }
    public void Dispose()
    {
        lock(_captureGate)
        {
            if(_disposed) return;
            _disposed=true; _input.Dispose(); _frames.Dispose(); _queries.Dispose();
        }
    }
    private sealed class OwnerActions(OwnerInputQueue input,TerminalSession session,IHostActions? target) : IHostActions
    {
        public IHostActions? Target=target;
        public (Color Foreground,Color Background)? Colors;
        public IReadOnlyList<Color>? Palette;
        public Color? Cursor;
        public (Color Foreground,Color Background)? DefaultColors => Colors ?? Target?.DefaultColors;
        public IReadOnlyList<Color>? DefaultPalette => Palette ?? Target?.DefaultPalette;
        public Color? DefaultCursorColor => Cursor ?? Target?.DefaultCursorColor;
        public void Respond(string reply)
        { if(input.Submit(new TerminalInput.Raw(Encoding.UTF8.GetBytes(reply))) != TerminalInputAcceptance.Accepted) session.MarkInputClosed(); }
        public void Notify(string title,string body) => Target?.Notify(title,body);
        public void Progress(int state,int value) => Target?.Progress(state,value);
        public void ClipboardWrite(string text) => Target?.ClipboardWrite(text);
        public void Bell() => Target?.Bell();
        public void CommandFinished(string token,long sequence) => Target?.CommandFinished(token,sequence);
        public void Unhandled(string kind,string detail) => Target?.Unhandled(kind,detail);
    }
}
