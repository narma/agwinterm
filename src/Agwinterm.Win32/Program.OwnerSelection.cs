using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Win32;

internal partial class Program
{
    private sealed class OwnerSelection : IDisposable
    {
        private readonly ISessionPresentation _presentation;
        private readonly TerminalQuery _query;
        private readonly CancellationTokenSource _cancel=new();
        private readonly Action _changed;
        private readonly string? _fallback;
        private readonly bool _block;
        private sealed record Snapshot(long Generation,TerminalSelectionResult Value,bool Confirmed);
        private Snapshot? _snapshot;
        private TerminalFrame.Lease? _locatedFrame;
        private Snapshot? _locatedSelection;
        private int _refreshing,_disposed;
        private long _requestedGeneration=-1;
        public Task Ready { get; }
        private string _creationStatus="error";
        public string? CreationError => _creationStatus switch
        {
            "ok" => null,
            "stale" => "Terminal content changed; try selecting command output again",
            "limit" => "Command output exceeds the selection limit; select a smaller range to copy",
            _ => "Could not read command output; try again"
        };
        public bool CopyQueued;
        public async Task<string?> FormatAsync(int format)
        {
            await Ready.ConfigureAwait(false);
            return Formats?.Get(format);
        }
        public SelectionFormats? Formats { get; private set; }
        public string? Text => Volatile.Read(ref _snapshot)?.Value.Text ?? _fallback;
        public OwnerSelection(ISessionPresentation presentation,TerminalFrame.Lease frame,
            TerminalSelectionRequest request,string? fallback,Action changed)
        {
            _presentation=presentation; _fallback=fallback; _changed=changed; _block=request.Block;
            var identity=frame.Identity;
            _query=new(identity.Incarnation,identity.AttachmentEpoch,identity.TerminalGeneration,
                "selection-create",Text:JsonSerializer.Serialize(request),ResultLimit:100000);
            _snapshot=new(identity.TerminalGeneration,new(request.Id,true,request.AnchorRow,request.AnchorColumn,
                request.FocusRow,request.FocusColumn,fallback),false);
            _locatedFrame=frame.Retain();
            Ready=ReadAsync(_query,true);
        }
        private async Task ReadAsync(TerminalQuery query,bool create)
        {
            try
            {
                var result=await _presentation.QueryAsync(query,_cancel.Token).ConfigureAwait(false);
                if(create) _creationStatus=result.Truncated ? "limit" : result.Status == "ok" ? "error" : result.Status;
                if(result.Status == "ok" && !result.Truncated && result.Rows.Count == 1 &&
                    JsonSerializer.Deserialize<TerminalSelectionResult>(result.Rows[0]) is { } value)
                {
                    if(create) _creationStatus="ok";
                    if(create && value.Valid && value.Text is { } text) Formats=new(text,value.Vt,value.Html);
                    if(!create) value=value with { Text=Text };
                    if(Volatile.Read(ref _disposed) == 0) Volatile.Write(ref _snapshot,new(result.Generation,value,true));
                }
            }
            catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException)
            { System.Diagnostics.Trace.WriteLine("Selection query ended: "+ex.Message); }
            finally { Interlocked.Exchange(ref _refreshing,0); _changed(); }
        }
        public TerminalSelectionResult? Locate(TerminalFrame.Lease frame)
        {
            long generation=frame.Identity.TerminalGeneration;
            var snapshot=Volatile.Read(ref _snapshot);
            if (_locatedSelection is null && snapshot is { Confirmed:true, Value.Valid:true } &&
                snapshot.Generation == _locatedFrame?.Identity.TerminalGeneration)
                _locatedSelection=snapshot;
            if(snapshot is not null && snapshot.Generation == generation)
            {
                if(snapshot is { Confirmed:true, Value.Valid:true } && _locatedFrame?.Identity != frame.Identity)
                {
                    _locatedFrame?.Dispose();
                    _locatedFrame=frame.Retain();
                    _locatedSelection=snapshot;
                }
                return snapshot.Value;
            }
            if(snapshot is { Value.Valid:true } && Ready.IsCompleted && generation != _requestedGeneration &&
                Interlocked.CompareExchange(ref _refreshing,1,0) == 0)
            {
                _requestedGeneration=generation;
                _=ReadAsync(_query with { Operation="selection-locate" },false);
            }
            // The owner's reply can trail output that changed only the cursor or other rows.
            // Keep painting the last confirmed range while its visible cells still match.
            if(snapshot is { Value.Valid:true } && _locatedSelection is { } located &&
                _locatedFrame is { } previous && MatchesLocatedCells(previous,frame,located.Value))
                return located.Value;
            return null;
        }

        private bool MatchesLocatedCells(TerminalFrame.Lease previous, TerminalFrame.Lease current,
            TerminalSelectionResult selection)
        {
            var before = previous.State;
            var after = current.State;
            if (previous.Identity.ViewportRevision != current.Identity.ViewportRevision ||
                before.Columns != after.Columns || before.Rows != after.Rows ||
                before.HistoryRows != after.HistoryRows || before.ScrollOffset != after.ScrollOffset ||
                before.ScrollGeneration != after.ScrollGeneration || before.AlternateScreen != after.AlternateScreen)
                return false;

            var start = (Row: selection.AnchorRow, Column: selection.AnchorColumn);
            var end = (Row: selection.FocusRow, Column: selection.FocusColumn);
            if (start.CompareTo(end) > 0) (start, end) = (end, start);
            int top = after.HistoryRows - after.ScrollOffset;
            if (start.Row < top || end.Row - top >= after.Rows ||
                (uint)start.Column >= after.Columns || (uint)end.Column >= after.Columns)
                return false;

            for (int row = start.Row - top; row <= end.Row - top; row++)
            {
                int from = _block ? Math.Min(start.Column, end.Column) : row == start.Row - top ? start.Column : 0;
                int to = _block ? Math.Max(start.Column, end.Column) : row == end.Row - top ? end.Column : after.Columns - 1;
                if (!_block && row < end.Row - top && previous.WrapLength(row) != current.WrapLength(row))
                    return false;
                for (int column = from; column <= to; column++)
                {
                    var oldCell = previous[row,column];
                    var newCell = current[row,column];
                    if (oldCell.Rune != newCell.Rune || oldCell.Width != newCell.Width || oldCell.Grapheme != newCell.Grapheme)
                        return false;
                }
            }
            return true;
        }
        public void Dispose()
        {
            if(Interlocked.Exchange(ref _disposed,1) != 0) return;
            _locatedFrame?.Dispose();
            _locatedFrame=null;
            _cancel.Cancel();
            _=ReleaseAsync();
        }
        private async Task ReleaseAsync()
        {
            try { await Ready.ConfigureAwait(false); await _presentation.QueryAsync(_query with {Operation="selection-release"},default).ConfigureAwait(false); }
            catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
}
