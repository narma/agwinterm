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
        private sealed record Snapshot(long Generation,TerminalSelectionResult Value);
        private Snapshot? _snapshot;
        private int _refreshing,_disposed;
        private long _requestedGeneration=-1;
        public Task Ready { get; }
        public bool CopyQueued;
        public string? Text => Volatile.Read(ref _snapshot)?.Value.Text ?? _fallback;
        public OwnerSelection(ISessionPresentation presentation,TerminalFrameIdentity identity,
            TerminalSelectionRequest request,string? fallback,Action changed)
        {
            _presentation=presentation; _fallback=fallback; _changed=changed;
            _query=new(identity.Incarnation,identity.AttachmentEpoch,identity.TerminalGeneration,
                "selection-create",Text:JsonSerializer.Serialize(request),ResultLimit:100000);
            _snapshot=new(identity.TerminalGeneration,new(request.Id,true,request.AnchorRow,request.AnchorColumn,
                request.FocusRow,request.FocusColumn,fallback));
            Ready=ReadAsync(_query,true);
        }
        private async Task ReadAsync(TerminalQuery query,bool create)
        {
            try
            {
                var result=await _presentation.QueryAsync(query,_cancel.Token).ConfigureAwait(false);
                if(result.Status == "ok" && result.Rows.Count == 1 &&
                    JsonSerializer.Deserialize<TerminalSelectionResult>(result.Rows[0]) is { } value)
                {
                    if(!create) value=value with { Text=Text };
                    if(Volatile.Read(ref _disposed) == 0) Volatile.Write(ref _snapshot,new(result.Generation,value));
                }
            }
            catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException)
            { System.Diagnostics.Trace.WriteLine("Selection query ended: "+ex.Message); }
            finally { Interlocked.Exchange(ref _refreshing,0); _changed(); }
        }
        public TerminalSelectionResult? Locate(long generation)
        {
            var snapshot=Volatile.Read(ref _snapshot);
            if(snapshot is not null && snapshot.Generation == generation) return snapshot.Value;
            if(snapshot is { Value.Valid:true } && Ready.IsCompleted && generation != _requestedGeneration &&
                Interlocked.CompareExchange(ref _refreshing,1,0) == 0)
            {
                _requestedGeneration=generation;
                _=ReadAsync(_query with { Operation="selection-locate" },false);
            }
            return null;
        }
        public void Dispose()
        {
            if(Interlocked.Exchange(ref _disposed,1) != 0) return;
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
