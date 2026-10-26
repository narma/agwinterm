using System.Runtime.InteropServices;
using System.Text;

namespace Agwinterm.Core;

/// <summary>
/// An immutable text selection for a viewport owned by a TUI. Locate its cell spans and
/// row context in later frames; keep the copied text when content is absent or ambiguous.
/// Works with either core's managed screen, without guessing how far a wheel event scrolls.
/// </summary>
public sealed class ContentSelection
{
    private sealed class Pattern(int from, uint[] cells, int rows)
    {
        public readonly int From = from;
        public readonly uint[] Cells = cells;
        public readonly int Ink = cells.Count(cell => (cell >> 21) > 0 && (cell & 0x1fffff) is not (0 or 32));
        // Unknown / equal / different. Populated only for candidate rows actually visited.
        public sbyte[] Matches = new sbyte[rows];
    }

    private readonly int _cols, _originalRows;
    private uint[] _frame;
    private readonly Pattern[] _selected, _wholeRows, _patterns;
    private readonly Pattern? _before, _after;
    private int _lastTop;
    private bool _hasResult = true, _lastVisible = true;
    private string? _text;
    public int Height => _selected.Length;

    // A drag replaces its anchor on every pointer move. Build the copy string only when needed,
    // not for all of those intermediate selections, and never allocate a string per BMP cell.
    public string Text => _text ??= BuildText();

    public ContentSelection(ScreenBuffer screen, int first, int startCol, int last, int endCol, bool block)
    {
        _cols = screen.Cols; _originalRows = screen.Rows;
        _frame = new uint[_cols * _originalRows];
        screen.CopyTextTo(_frame);
        _lastTop = first;
        _selected = new Pattern[last - first + 1];
        _wholeRows = new Pattern[_selected.Length];

        // Intern equal spans once, including their column interval. Repeated lines in a large
        // selection then share one exact comparison per viewport row, not one per candidate.
        // Hashes only find a bucket; SequenceEqual resolves collisions and defines equality.
        var buckets = new Dictionary<(int from, int length, int hash), List<Pattern>>();
        var patterns = new List<Pattern>();
        Pattern Intern(int row, int from, int to)
        {
            var cells = _frame.AsSpan(row * _cols + from, to - from + 1);
            var hash = new HashCode();
            hash.AddBytes(MemoryMarshal.AsBytes(cells));
            var key = (from, cells.Length, hash.ToHashCode());
            if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new();
            foreach (var existing in bucket)
                if (cells.SequenceEqual(existing.Cells)) return existing;
            var pattern = new Pattern(from, cells.ToArray(), _originalRows);
            bucket.Add(pattern); patterns.Add(pattern);
            return pattern;
        }
        for (int row = first; row <= last; row++)
        {
            int from = block ? Math.Min(startCol, endCol) : row == first ? startCol : 0;
            int to = block ? Math.Max(startCol, endCol) : row == last ? endCol : _cols - 1;
            _selected[row - first] = Intern(row, from, to);
            _wholeRows[row - first] = Intern(row, 0, _cols - 1);
        }
        if (first > 0) _before = Intern(first - 1, 0, _cols - 1);
        if (last + 1 < _originalRows) _after = Intern(last + 1, 0, _cols - 1);
        _patterns = patterns.ToArray();
    }

    private string BuildText()
    {
        var text = new StringBuilder();
        for (int row = 0; row < Height; row++)
        {
            int lineStart = text.Length;
            foreach (uint cell in _selected[row].Cells)
            {
                if ((cell >> 21) == 0) continue;
                int rune = (int)(cell & 0x1fffff);
                if (rune <= 0xffff) text.Append(rune == 0 ? ' ' : (char)rune);
                else
                {
                    rune -= 0x10000;
                    text.Append((char)(0xd800 + (rune >> 10)));
                    text.Append((char)(0xdc00 + (rune & 0x3ff)));
                }
            }
            while (text.Length > lineStart && text[^1] == ' ') text.Length--;
            if (row + 1 < Height) text.Append("\r\n");
        }
        return text.ToString();
    }

    /// <summary>Called under the session lock. False retains Text but paints no stale cells.</summary>
    public bool TryLocate(ScreenBuffer screen, out int top)
    {
        top = _lastTop;
        if (screen.Cols != _cols) { _hasResult = false; return false; }
        int rows = screen.Rows;
        if (!_lastVisible && Height == 1)
        {
            // Missing short selections are common while scrolled away. A failed word match
            // usually needs one cell per row, much less than refreshing the full viewport.
            bool present = false;
            var word = _selected[0];
            for (int row = 0; row < rows && !present; row++)
                present = screen.TextMatches(row, word.From, word.Cells);
            if (!present) { _hasResult = false; return false; }
        }
        if (_frame.Length != rows * _cols)
        {
            _frame = new uint[rows * _cols];
            foreach (var pattern in _patterns) pattern.Matches = new sbyte[rows];
            _hasResult = false;
        }
        bool changed = screen.CopyTextTo(_frame);
        // Cache negative results too: a hidden/ambiguous selection need not be searched again
        // on every cursor blink. Styling changes do not affect text identity.
        if (!changed && _hasResult) return _lastVisible;
        _hasResult = true;
        foreach (var pattern in _patterns) Array.Clear(pattern.Matches);

        int bestScore = 0, best = 0;
        bool ambiguous = false;
        for (int candidate = 1 - Height; candidate < rows; candidate++)
        {
            int visibleInk = 0, context = 0;
            bool matches = true;
            int firstVisible = Math.Max(0, -candidate), lastVisible = Math.Min(Height, rows - candidate);
            for (int i = firstVisible; i < lastVisible; i++)
            {
                int target = candidate + i;
                if (!RowMatches(_selected[i], target)) { matches = false; break; }
                visibleInk += _selected[i].Ink;
                if (RowMatches(_wholeRows[i], target)) context++;
            }
            if (!matches || visibleInk == 0) continue;
            if (_before is not null && candidate > 0 && RowMatches(_before, candidate - 1)) context++;
            if (_after is not null && candidate + Height < rows && RowMatches(_after, candidate + Height)) context++;
            // A common word alone is insufficient evidence of the selected occurrence moving.
            if (context == 0) continue;
            int score = visibleInk * (_originalRows + 3) + context;
            if (score > bestScore) { bestScore = score; best = candidate; ambiguous = false; }
            else if (score == bestScore) ambiguous = true;
        }
        _lastVisible = bestScore > 0 && !ambiguous;
        if (_lastVisible) top = _lastTop = best;
        return _lastVisible;
    }

    private bool RowMatches(Pattern pattern, int row)
    {
        // Lazy memoization also avoids a patterns × rows full pass for near-identical lines:
        // most candidate positions can be rejected by their first selected span alone.
        ref sbyte result = ref pattern.Matches[row];
        if (result == 0)
            result = _frame.AsSpan(row * _cols + pattern.From, pattern.Cells.Length)
                .SequenceEqual(pattern.Cells) ? (sbyte)1 : (sbyte)-1;
        return result > 0;
    }
}
