using System.Text;

namespace Agwinterm.Core;

/// <summary>Streaming, invariant Unicode simple-case search over native terminal cells.
/// No normalization or multi-scalar case expansion; coordinates always cover whole cells.
/// Memory is bounded by the query and result limit, independent of scrollback size.</summary>
internal sealed class TerminalTextSearch
{
    private readonly Rune[] _needle;
    private readonly int[] _fallback;
    private readonly (int Line, int Col)[] _starts;
    private int _matched, _position;
    internal const int MatchLimit = 100_000;
    internal List<(int Line, int Col0, int EndLine, int Col1)> Matches { get; } = [];
    internal bool Truncated { get; private set; }

    internal TerminalTextSearch(string query)
    {
        if (query.Length > 65536) throw new ArgumentOutOfRangeException(nameof(query));
        _needle = query.EnumerateRunes().Select(Rune.ToUpperInvariant).ToArray();
        _fallback = new int[_needle.Length];
        _starts = new (int, int)[_needle.Length];
        for (int i = 1, j = 0; i < _needle.Length; i++)
        {
            while (j > 0 && _needle[i] != _needle[j]) j = _fallback[j - 1];
            if (_needle[i] == _needle[j]) j++;
            _fallback[i] = j;
        }
    }

    internal void AddRow(int line, ReadOnlySpan<Cell> cells, int wrap, CancellationToken cancellation = default)
    {
        if (_needle.Length == 0 || Truncated) return;
        int length = wrap < 0 ? cells.Length : Math.Min(wrap, cells.Length);
        for (int col = 0; col < length && !Truncated; col++)
        {
            if ((col & 255) == 0) cancellation.ThrowIfCancellationRequested();
            var cell = cells[col];
            if (cell.Width == 0) continue;
            if (cell.Grapheme is { } cluster)
                foreach (var rune in cluster.EnumerateRunes()) Add(rune, line, col, cell.Width);
            else Add(new Rune(cell.Rune == 0 ? ' ' : cell.Rune), line, col, cell.Width);
        }
        if (wrap < 0) _matched = 0; // A hard newline cannot be crossed by a literal query.
    }

    private void Add(Rune rune, int line, int col, int width)
    {
        if (Truncated) return;
        rune = Rune.ToUpperInvariant(rune);
        _starts[_position] = (line, col);
        _position = (_position + 1) % _needle.Length;
        while (_matched > 0 && rune != _needle[_matched]) _matched = _fallback[_matched - 1];
        if (rune == _needle[_matched]) _matched++;
        if (_matched != _needle.Length) return;
        var start = _starts[_position];
        var match = (start.Line, start.Col, line, col + width - 1);
        if (Matches.Count == 0 || Matches[^1] != match)
        {
            if (Matches.Count == MatchLimit) Truncated = true;
            else Matches.Add(match);
        }
        _matched = _fallback[_matched - 1];
    }
}
