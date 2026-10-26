using System.Text;

namespace Agwinterm.Core;

/// <summary>
/// An immutable text selection for a viewport owned by a TUI. Unlike scrollback indices,
/// screen coordinates do not identify content after SU/SD or an application redraw.
/// Keep the copied text and locate its cell spans in subsequent frames. Whole rows and
/// neighbours disambiguate repeated words; ambiguous or absent content is not highlighted.
/// Works on either core's managed screen, without guessing how far a wheel event scrolls.
/// </summary>
public sealed class ContentSelection
{
    private readonly Cell[][] _original;
    private Cell[][] _lastFrame;
    private readonly int _first, _last, _startCol, _endCol;
    private readonly bool _block;
    private int _lastTop;
    public string Text { get; }
    public int Height => _last - _first + 1;

    public ContentSelection(ScreenBuffer screen, int first, int startCol, int last, int endCol, bool block)
    {
        _original = Snapshot(screen);
        _lastFrame = _original;
        _first = first; _last = last; _startCol = startCol; _endCol = endCol; _block = block;
        _lastTop = first;
        var text = new StringBuilder();
        for (int row = first; row <= last; row++)
        {
            var (from, to) = Columns(row);
            var line = new StringBuilder();
            for (int col = from; col <= to; col++)
            {
                var cell = _original[row][col];
                if (cell.Width == 0) continue;
                line.Append(char.ConvertFromUtf32(cell.Rune == 0 ? ' ' : cell.Rune));
            }
            text.Append(line.ToString().TrimEnd(' '));
            if (row < last) text.Append("\r\n");
        }
        Text = text.ToString();
    }

    private (int from, int to) Columns(int row) => _block
        ? (Math.Min(_startCol, _endCol), Math.Max(_startCol, _endCol))
        : (row == _first ? _startCol : 0, row == _last ? _endCol : _original[0].Length - 1);

    private static Cell[][] Snapshot(ScreenBuffer screen)
    {
        var rows = new Cell[screen.Rows][];
        for (int row = 0; row < rows.Length; row++)
        {
            rows[row] = new Cell[screen.Cols];
            screen.CopyRowTo(row, rows[row]);
        }
        return rows;
    }

    private static bool Same(Cell a, Cell b) => a.Rune == b.Rune && a.Width == b.Width;
    private static bool RowMatches(Cell[] row, ScreenBuffer screen, int at)
    {
        if (at < 0 || at >= screen.Rows || row.Length != screen.Cols) return false;
        for (int col = 0; col < row.Length; col++)
            if (!Same(row[col], screen[at, col])) return false;
        return true;
    }

    /// <summary>Called under the session lock. False retains Text but paints no stale cells.</summary>
    public bool TryLocate(ScreenBuffer screen, out int top)
    {
        top = _lastTop;
        if (screen.Cols != _original[0].Length) return false; // reflow needs a separate mapping
        bool unchanged = _lastFrame.Length == screen.Rows;
        for (int row = 0; unchanged && row < screen.Rows; row++)
            unchanged = RowMatches(_lastFrame[row], screen, row);
        if (unchanged) return true;

        int bestScore = 0, best = 0;
        bool ambiguous = false;
        for (int candidate = 1 - Height; candidate < screen.Rows; candidate++)
        {
            int visibleInk = 0, context = 0;
            bool matches = true;
            for (int i = 0; i < Height; i++)
            {
                int target = candidate + i, source = _first + i;
                if (target < 0 || target >= screen.Rows) continue;
                var (from, to) = Columns(source);
                for (int col = from; col <= to; col++)
                {
                    Cell cell = _original[source][col];
                    if (!Same(cell, screen[target, col])) { matches = false; break; }
                    if (cell.Width > 0 && cell.Rune is not (0 or 32)) visibleInk++;
                }
                if (!matches) break;
                if (RowMatches(_original[source], screen, target)) context++;
            }
            if (!matches || visibleInk == 0) continue;
            if (_first > 0 && RowMatches(_original[_first - 1], screen, candidate - 1)) context++;
            if (_last + 1 < _original.Length && RowMatches(_original[_last + 1], screen, candidate + Height)) context++;
            // A lone matching word in an otherwise unrelated frame is not evidence that the
            // selected occurrence moved there. Require its row or a neighbouring row as well.
            if (context == 0) continue;
            int score = visibleInk * (_original.Length + 3) + context;
            if (score > bestScore) { bestScore = score; best = candidate; ambiguous = false; }
            else if (score == bestScore) ambiguous = true;
        }
        if (bestScore == 0 || ambiguous) return false;
        top = _lastTop = best;
        _lastFrame = Snapshot(screen);
        return true;
    }
}
