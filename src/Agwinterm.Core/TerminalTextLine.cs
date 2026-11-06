using System.Text;

namespace Agwinterm.Core;

/// <summary>Maps UTF-16 text ranges to cells without splitting a terminal grapheme.</summary>
public sealed class TerminalTextLine
{
    public string Text { get; }
    private readonly int[] _offsets, _starts, _ends;
    public TerminalTextLine(ReadOnlySpan<Cell> cells)
    {
        _offsets = new int[cells.Length + 1];
        var text = new StringBuilder(cells.Length);
        var starts = new List<int>(); var ends = new List<int>();
        int previous = 0;
        for (int col = 0; col < cells.Length; col++)
        {
            var cell = cells[col];
            if (cell.Width == 0) { _offsets[col] = previous; continue; }
            previous = text.Length; _offsets[col] = previous;
            string cluster = cell.Text;
            text.Append(cluster);
            for (int i = 0; i < cluster.Length; i++)
            { starts.Add(col); ends.Add(Math.Min(cells.Length, col + cell.Width)); }
        }
        _offsets[cells.Length] = text.Length;
        Text = text.ToString(); _starts = starts.ToArray(); _ends = ends.ToArray();
    }
    public int OffsetAtColumn(int column) => _offsets[Math.Clamp(column, 0, _offsets.Length - 1)];
    public int ColumnAtOffset(int offset, bool end = false)
    {
        offset = Math.Clamp(offset, 0, Text.Length);
        if (end) return offset == 0 ? 0 : _ends[offset - 1];
        return offset == Text.Length ? _offsets.Length - 1 : _starts[offset];
    }
}
