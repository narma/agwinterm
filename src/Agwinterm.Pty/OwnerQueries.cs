using Agwinterm.Core;
using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>Bounded owner reads shared by direct and transported sessions. No
/// history is installed as a live UI terminal; callers page at a fenced generation.</summary>
internal sealed partial class OwnerQueries(TerminalSession session, string incarnation, ulong epoch) : IDisposable
{
    public async ValueTask<TerminalQueryResult> ReadAsync(TerminalQuery query, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        TerminalQueryResult Result(string status, long generation = 0, IReadOnlyList<string>? rows = null,
            bool truncated = false, IReadOnlyList<TerminalSearchMatch>? matches = null)
            => new(incarnation,epoch,generation,status,rows ?? [],truncated,matches);
        if (query.Incarnation != incarnation || query.AttachmentEpoch != epoch) return Result("stale");
        int maximum=query.Operation == "styled-buffer" && query.RowCount == 1 ? PresentationQueryWire.MaximumBytes-1024 : 100000;
        if (query.Generation < 0 || query.ResultLimit < 1 || query.ResultLimit > maximum || query.Text.Length > 4096 ||
            query.StartRow < 0 || query.RowCount is < 0 or > 32) return Result("invalid");
        var core = (GhosttyTerminalCore)session.Emulator;
        try
        {
            if(query.Operation == "last-output")
                return await Task.Run(()=>
                {
                    lock(session.SyncRoot)
                    {
                        var mark=core.Marks.LastOrDefault(m=>m.EndLine >= 0);
                        if(mark is null) return Result("ok",core.Generation,["no completed command marks (FTCS wrap not active?)"]);
                        int first=mark.OutputLine >= 0 ? mark.OutputLine : mark.CommandLine >= 0 ? mark.CommandLine+1 : mark.PromptLine+1;
                        var text=new System.Text.StringBuilder();
                        for(int row=Math.Max(0,first);row<mark.EndLine && row<core.HistoryCount+core.Rows;row++)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if(text.Length > 0) text.Append("\r\n");
                            text.Append(row < core.HistoryCount ? core.DumpHistoryRow(row) : core.DumpRow(row-core.HistoryCount));
                            if(text.Length > query.ResultLimit) return Result("limit",core.Generation);
                        }
                        return Result("ok",core.Generation,[text.ToString()]);
                    }
                },cancellation).ConfigureAwait(false);
            if(query.Operation.StartsWith("selection-",StringComparison.Ordinal))
                return await Task.Run(()=>ReadSelection(query,cancellation),cancellation).ConfigureAwait(false);
            if (query.Operation == "search")
            {
                if (query.Text.Length == 0) return Result("invalid");
                var found = await core.SearchAsync(query.Text,session.SyncRoot,cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                var matches=found.Matches.Take(query.ResultLimit).Select(m => new TerminalSearchMatch(m.Line,m.Col0,m.EndLine,m.Col1)).ToArray();
                lock (session.SyncRoot)
                {
                    if (core.Generation != found.Version) return Result("stale",core.Generation);
                    return Result("ok",found.Version,truncated:found.Truncated || found.Matches.Count > query.ResultLimit,
                        matches:matches);
                }
            }
            if (query.Operation is not ("history" or "viewport" or "buffer" or "styled-buffer" or "text-cells")) return Result("unsupported");
            return await Task.Run(() =>
            {
                var rows = new List<string>(); int characters = 0;
                // Release between rows. Output may proceed, in which case reject
                // the whole page instead of assembling different generations.
                for (int offset = 0; offset < query.RowCount; offset++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    lock (session.SyncRoot)
                    {
                        if (core.Generation != query.Generation) return Result("stale",core.Generation);
                        long row = (long)query.StartRow+offset;
                        int history=core.HistoryCount;
                        int available = query.Operation is "buffer" or "styled-buffer" or "text-cells" ? history+core.Rows : query.Operation == "history" ? history : core.Rows;
                        if (row >= available) break;
                        string text = query.Operation == "text-cells" ? JsonSerializer.Serialize(Enumerable.Range(0,core.Cols).Select(col=>
                            { var cell=row < history ? core.GetHistoryCell((int)row,col) : core.Screen[(int)row-history,col];
                              return new TerminalTextCell(cell.Text,cell.Width); }))
                            : query.Operation == "styled-buffer" ? SurfaceText.SerializeStyledRow(core,(int)row,history,core.Cols)
                            : query.Operation == "buffer"
                            ? row < history ? core.DumpHistoryRow((int)row) : core.DumpRow((int)row-history)
                            : query.Operation == "history" ? core.DumpHistoryRow((int)row) : core.DumpRow((int)row);
                        if (text.Length > query.ResultLimit-characters) return Result("ok",query.Generation,rows,true);
                        rows.Add(text); characters += text.Length;
                    }
                }
                lock (session.SyncRoot)
                    return core.Generation == query.Generation ? Result("ok",query.Generation,rows) : Result("stale",core.Generation);
            },cancellation).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { return Result("closed"); }
    }
}
