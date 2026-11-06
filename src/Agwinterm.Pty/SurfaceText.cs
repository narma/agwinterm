using System.Text;
using System.Text.Json;
using Agwinterm.Core;

namespace Agwinterm.Pty;

/// <summary>
/// The ONE reader behind <c>session text</c> and <c>session overlay text</c> (P5): a surface's buffer
/// as plain text (or attribute runs for <c>session text --styles</c>), trailing blank lines trimmed.
/// Exposed here rather than kept private to the server
/// because the app host reads an overlay's terminal with it too — a second walk would be a second
/// answer to "what is on this surface".
/// </summary>
public static class SurfaceText
{
    /// <summary>
    /// Dump <paramref name="s"/>'s buffer. <see cref="OverlayTextArgs.All"/>: every history row and
    /// then the screen. Else <see cref="OverlayTextArgs.Lines"/> reaches back into scrollback — the
    /// last N lines ending at the bottom of the visible screen, so an N larger than the screen height
    /// picks up history; 0 keeps the old meaning exactly: the visible screen.
    ///
    /// Scrollback matters because the interesting part is usually already gone: a launch banner, a
    /// version, an error printed before a full-screen app took the alt screen. An agent reading a
    /// pane it did not watch could reach none of it.
    /// </summary>
    public static string Dump(ISession s, OverlayTextArgs args)
    {
        if(s is ServerSession owner) owner.AdvanceHiddenReadback();
        if(s.Presentation is { IsRemote:true })
            return DumpRemoteAsync(s,args).GetAwaiter().GetResult();
        return DumpCached(s,args);
    }

    private static string DumpCached(ISession s,OverlayTextArgs args)
    {
        var sb = new StringBuilder();
        lock (s.SyncRoot)
        {
            var em = s.Emulator;
            int rows = em.Screen.Rows, hist = em.HistoryCount;
            // Absolute numbering: [0, hist) is scrollback, then the live rows.
            for (int abs = FirstRow(rows, hist, args); abs < hist + rows; abs++)
                sb.Append(abs < hist ? em.DumpHistoryRow(abs) : em.DumpRow(abs - hist)).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Pipe-worker read: bounded pages from one owner generation. Never
    /// hold the UI/session lock while waiting for transport or return a mixed buffer.</summary>
    private static async Task<string> DumpRemoteAsync(ISession session,OverlayTextArgs args,bool styled=false)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for(int attempt=0;attempt<4;attempt++)
        {
            TerminalFrame.Lease? captured;
            lock(session.SyncRoot)
            {
                var view = (PresentedTerminalView)session.Emulator;
                if (!args.All && args.Lines <= 0 && view.ViewportOffset == 0)
                    return styled ? DumpStyledCached(session,args) : DumpCached(session,args);
                captured=view.AcquireFrame();
                // Startup has no history yet, and a failed launch may have a
                // literal error view with no owner. Both are readable surfaces.
                if(captured is null) return styled ? DumpStyledCached(session,args) : DumpCached(session,args);
            }
            using var frame=captured;
            var state=frame.State;
            int first=FirstRow(state.Rows,state.HistoryRows,args),end=checked(state.HistoryRows+state.Rows);
            int pageSize=Math.Clamp(100000/state.Columns,1,32);
            var result=new StringBuilder(); var styledRows=new List<StyledRow>(); bool stale=false;
            int totalCharacters=0;
            for(int row=first;row<end;)
            {
                int count=Math.Min(pageSize,end-row);
                var query=new TerminalQuery(frame.Identity.Incarnation,frame.Identity.AttachmentEpoch,frame.Identity.TerminalGeneration,
                    styled ? "styled-buffer" : "buffer",row,count);
                var page=await session.Presentation!.QueryAsync(query,deadline.Token).ConfigureAwait(false);
                if(styled && page.Status == "ok" && page.Truncated && page.Rows.Count == 0)
                {
                    // One densely styled row can exceed the normal page budget.
                    // Try just that row, still bounded by the transport record.
                    count=1;
                    page=await session.Presentation.QueryAsync(query with {RowCount=1,ResultLimit=PresentationQueryWire.MaximumBytes-1024},deadline.Token).ConfigureAwait(false);
                }
                if(page.Status == "stale") { stale=true; break; }
                if(page.Status != "ok" || page.Rows.Count == 0 || page.Rows.Count > count ||
                    !page.Truncated && page.Rows.Count != count || page.Generation != frame.Identity.TerminalGeneration)
                    throw new IOException("Owner buffer query failed: "+page.Status);
                foreach(string line in page.Rows)
                {
                    totalCharacters=checked(totalCharacters+line.Length+1);
                    if(totalCharacters > 16*1024*1024) throw new IOException("Terminal text exceeds the readback limit.");
                    if(styled) styledRows.Add(JsonSerializer.Deserialize<StyledRow>(line,StyledJson)
                        ?? throw new InvalidDataException("Empty styled owner row."));
                    else result.Append(line).Append('\n');
                }
                row+=page.Rows.Count;
            }
            if(!stale)
            {
                if(!styled) return result.ToString().TrimEnd('\n');
                while(styledRows.Count > 0 && styledRows[^1].Runs.Count == 0) styledRows.RemoveAt(styledRows.Count-1);
                return JsonSerializer.Serialize(new { cols=state.Columns,rows=styledRows,
                    cursor=new { row=state.CursorRow,col=state.CursorColumn } },StyledJson);
            }
            await Task.Delay(16,deadline.Token).ConfigureAwait(false);
        }
        throw new IOException("Terminal changed during readback; retry the request.");
    }

    private static int FirstRow(int rows, int hist, OverlayTextArgs args)
    {
        int take = args.All ? rows + hist : args.Lines <= 0 ? rows : Math.Min(args.Lines, rows + hist);
        return hist + rows - take;
    }

    private static readonly JsonSerializerOptions StyledJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record StyledRow(int Row, List<StyledRun> Runs);
    private sealed record StyledRun(int Col, int Width, string Text, bool Faint, bool Bold,
        bool Italic, bool Underline, bool Inverse, bool Strike, string Fg, string Bg);

    /// <summary>JSON text runs and cursor from one locked snapshot. Text trims exactly like
    /// <see cref="Dump"/> (including styled trailing whitespace); run extents use grid columns,
    /// not UTF-16 or codepoint counts. Row zero is the screen top; negative rows are history.</summary>
    public static string DumpStyled(ISession s, OverlayTextArgs args)
    {
        if(s is ServerSession owner) owner.AdvanceHiddenReadback();
        if(s.Presentation is { IsRemote:true })
            return DumpRemoteAsync(s,args,styled:true).GetAwaiter().GetResult();
        return DumpStyledCached(s,args);
    }

    private static string DumpStyledCached(ISession s,OverlayTextArgs args)
    {
        lock (s.SyncRoot)
        {
            var em = s.Emulator;
            int rows = em.Screen.Rows, cols = em.Screen.Cols, hist = em.HistoryCount;
            var result = new List<StyledRow>();
            for (int abs = FirstRow(rows, hist, args); abs < hist + rows; abs++)
                result.Add(ReadStyledRow(em,abs,hist,cols));
            while (result.Count > 0 && result[^1].Runs.Count == 0) result.RemoveAt(result.Count - 1);
            return JsonSerializer.Serialize(new { cols, rows = result,
                cursor = new { row = em.CursorRow, col = em.CursorCol } }, StyledJson);
        }
    }

    internal static string SerializeStyledRow(ITerminalCore em,int absoluteRow,int history,int columns)
        => JsonSerializer.Serialize(ReadStyledRow(em,absoluteRow,history,columns),StyledJson);

    private static StyledRow ReadStyledRow(ITerminalCore em,int abs,int hist,int cols)
    {
        Cell At(int col) => abs < hist ? em.GetHistoryCell(abs, col) : em.Screen[abs - hist, col];
        // DumpRow uses string.TrimEnd(): all Unicode whitespace, regardless of style.
        // No supplementary-plane codepoints are whitespace in .NET's TrimEnd set.
        int last = cols - 1;
        while (last >= 0)
        {
            var cell = At(last);
            if (cell.Width != 0 && (cell.Grapheme is not null || !(cell.Rune <= char.MaxValue && char.IsWhiteSpace((char)cell.Rune)))) break;
            last--;
        }

        var runs = new List<StyledRun>();
        var text = new StringBuilder();
        Cell style = default;
        int start = 0, end = 0;
        void Flush()
        {
            if (text.Length == 0) return;
            var a = style.Attributes;
            runs.Add(new(start, end - start, text.ToString(),
                (a & CellAttributes.Dim) != 0, (a & CellAttributes.Bold) != 0,
                (a & CellAttributes.Italic) != 0, (a & CellAttributes.Underline) != 0,
                (a & CellAttributes.Inverse) != 0, (a & CellAttributes.Strikethrough) != 0,
                Spec(style.FgSpec), Spec(style.BgSpec)));
            text.Clear();
        }
        for (int c = 0; c <= last; c++)
        {
            var cell = At(c);
            if (cell.Width == 0) continue;
            if (text.Length > 0 && (style.Attributes != cell.Attributes ||
                style.FgSpec != cell.FgSpec || style.BgSpec != cell.BgSpec)) Flush();
            if (text.Length == 0) { start = c; style = cell; }
            if (cell.Grapheme is { } cluster) text.Append(cluster);
            else if (cell.Rune > char.MaxValue) text.Append(char.ConvertFromUtf32(cell.Rune));
            else text.Append((char)cell.Rune);
            end = Math.Min(cols, c + cell.Width);
        }
        Flush();
        return new(abs-hist,runs);
    }

    private static string Spec(ColorSpec spec) => spec.Kind switch
    {
        ColorSpecKind.Indexed => $"idx:{spec.Index}",
        ColorSpecKind.Rgb => $"#{spec.Rgb.R:x2}{spec.Rgb.G:x2}{spec.Rgb.B:x2}",
        _ => "default",
    };
}
