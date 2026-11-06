using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Win32;

internal partial class Program
{
    private sealed record OwnerUiaCache(ISession Session,TerminalFrameIdentity Identity,Uia.TextSnapshot Snapshot);
    private OwnerUiaCache? _ownerUiaCache;

    private Uia.TextSnapshot? ReadOwnerAwareUiaSnapshot()
    {
        ISession? session=null; TerminalFrame.Lease? captured=null;
        var fallback=ReadUia(() =>
        {
            _uia.EnsureAlive();
            var result=BuildUiaTextSnapshot();
            if(ActiveSurface()?.S is { Presentation.IsRemote:true } remote)
            {
                session=remote;
                lock(remote.SyncRoot) captured=((PresentedTerminalView)remote.Emulator).AcquireFrame();
            }
            return result;
        });
        using var frame=captured;
        if(session is null || frame is null || fallback is null) return fallback;
        var cached=Volatile.Read(ref _ownerUiaCache);
        if(cached?.Session == session && cached.Identity == frame.Identity) return cached.Snapshot;
        // Synchronous UIA reentrancy while raising events must never wait for IPC.
        if(Environment.CurrentManagedThreadId == _uiaThreadId) return fallback;
        try
        {
            var snapshot=ReadOwnerUiaHistoryAsync(session,frame,fallback).GetAwaiter().GetResult();
            if(!ReadUia(()=>ReferenceEquals(ActiveSurface()?.S,session))) return null;
            Volatile.Write(ref _ownerUiaCache,new(session,frame.Identity,snapshot));
            _uia.EnsureAlive();
            return snapshot;
        }
        catch(Exception ex) when(ex is IOException or OperationCanceledException or JsonException or InvalidOperationException)
        { return fallback; }
    }

    private static async Task<Uia.TextSnapshot> ReadOwnerUiaHistoryAsync(ISession session,TerminalFrame.Lease frame,Uia.TextSnapshot geometry)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var state=frame.State;
        int retainedHistory=Math.Min(2000,Math.Max(0,1_000_000/state.Columns-state.Rows));
        int first=state.AlternateScreen ? state.HistoryRows : Math.Max(0,state.HistoryRows-retainedHistory);
        int end=checked(state.HistoryRows+state.Rows);
        var lines=new List<TerminalTextLine>(end-first);
        for(int row=first;row<end;)
        {
            // JSON carries cluster text and native cell widths only, not a second terminal.
            int count=Math.Min(Math.Clamp(100000/Math.Max(1,state.Columns*32),1,32),end-row);
            var reply=await session.Presentation!.QueryAsync(new(frame.Identity.Incarnation,frame.Identity.AttachmentEpoch,
                frame.Identity.TerminalGeneration,"text-cells",row,count),deadline.Token).ConfigureAwait(false);
            if(reply.Status != "ok" || reply.Truncated || reply.Generation != frame.Identity.TerminalGeneration || reply.Rows.Count != count)
                throw new IOException("Accessibility owner snapshot changed.");
            foreach(string encoded in reply.Rows)
            {
                var source=JsonSerializer.Deserialize<TerminalTextCell[]>(encoded)
                    ?? throw new InvalidDataException("Missing accessibility cells.");
                if(source.Length != state.Columns || source.Any(c=>c.Width > 2 || c.Text is null))
                    throw new InvalidDataException("Invalid accessibility row.");
                var cells=source.Select(c=>Cell.Empty with { Width=c.Width,Grapheme=c.Text }).ToArray();
                lines.Add(new TerminalTextLine(cells));
            }
            row+=count;
        }
        return OwnerUiaSnapshot(state,lines.ToArray(),first,geometry.ScreenX,geometry.ScreenY,geometry.CellW,geometry.CellH);
    }

    private static Uia.TextSnapshot OwnerUiaSnapshot(TerminalFrameState state,TerminalTextLine[] cells,int first,
        double x,double y,double cellWidth,double cellHeight)
    {
        var lines=cells.Select(c=>c.Text).ToArray();
        var starts=new int[lines.Length+1];
        for(int row=0;row<lines.Length;row++) starts[row+1]=checked(starts[row]+lines[row].Length+1);
        int caret=Math.Clamp(state.HistoryRows+state.CursorRow-first,0,lines.Length-1);
        return new Uia.TextSnapshot { Lines=lines,CellLines=cells,LineStart=starts,TotalLength=starts[^1]-1,
            CaretOffset=starts[caret]+cells[caret].OffsetAtColumn(state.CursorColumn),
            FirstVisibleLine=state.HistoryRows-state.ScrollOffset-first,VisibleRows=state.Rows,
            ScreenX=x,ScreenY=y,CellW=cellWidth,CellH=cellHeight };
    }
}
