using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Win32;

internal partial class Program
{
    private async Task SearchOwnerAsync(Pane pane,ISessionPresentation presentation,string text,
        CancellationTokenSource owner,bool preserveSelection)
    {
        var cancellation=owner.Token;
        try
        {
            await Task.Delay(preserveSelection ? 10 : 100,cancellation).ConfigureAwait(false);
            TerminalQuery query;
            lock (pane.S.SyncRoot)
            {
                using var frame=((PresentedTerminalView)pane.S.Emulator).AcquireFrame();
                if (frame is null) throw new InvalidOperationException("Owner viewport unavailable.");
                query=new(frame.Identity.Incarnation,frame.Identity.AttachmentEpoch,frame.Identity.TerminalGeneration,
                    "search",Text:text,ResultLimit:GhosttySearch.MatchLimit);
            }
            var result=await presentation.QueryAsync(query,cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            Post(() =>
            {
                if (!ReferenceEquals(_searchCancellation,owner) || !_searchActive) return;
                if (!ReferenceEquals(ActiveSurface(),pane)) { RecomputeSearch(); return; }
                if (result.Status == "stale") { RecomputeSearch(preserveSelection:true); return; }
                if (result.Status != "ok")
                { _searchPending=false; _searchError="search unavailable"; RequestRedraw(); return; }
                lock (pane.S.SyncRoot)
                {
                    using var current=((PresentedTerminalView)pane.S.Emulator).AcquireFrame();
                    if (current is null || current.Identity.Incarnation != result.Incarnation ||
                        current.Identity.AttachmentEpoch != result.AttachmentEpoch ||
                        current.Identity.TerminalGeneration > result.Generation)
                    { RecomputeSearch(preserveSelection:true); return; }
                }
                _searchVersion=result.Generation;
                _searchMatches.Clear();
                if (result.Matches is { } matches)
                    _searchMatches.AddRange(matches.Select(m=>(m.Line,m.Column,m.EndLine,m.EndColumn)));
                _searchCur=preserveSelection ? Math.Clamp(_searchCur,0,Math.Max(0,_searchMatches.Count-1)) : 0;
                _searchPending=false; _searchTruncated=result.Truncated;
                if (_searchNeedsScroll) { _searchNeedsScroll=false; ScrollToMatch(); }
                RequestRedraw();
            });
        }
        catch (OperationCanceledException)
        {
            if (!cancellation.IsCancellationRequested) Failed(); // owner query deadline
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { Failed(); }
        void Failed() => Post(() =>
        {
            if (!ReferenceEquals(_searchCancellation,owner)) return;
            _searchPending=false; _searchError="search unavailable"; RequestRedraw();
        });
    }
}
