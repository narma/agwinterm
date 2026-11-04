using System.Text;

namespace Agwinterm.Core;

/// <summary>Logical copy text from inclusive cell endpoints, preserving soft wraps and clusters.</summary>
public static class TerminalSelectionText
{
    internal static void AppendCell(StringBuilder text, Cell cell)
    {
        if (cell.Width == 0) return;
        if (cell.Grapheme is { } cluster) text.Append(cluster);
        else if (cell.Rune <= 0xffff) text.Append(cell.Rune == 0 ? ' ' : (char)cell.Rune);
        else text.Append(char.ConvertFromUtf32(cell.Rune));
    }
    public static string Read(ITerminalCore core, int anchorRow, int anchorCol, int focusRow, int focusCol, bool block)
    {
        int cols = core is GhosttyTerminalCore native ? native.Cols : core.Screen.Cols;
        int rows = core is GhosttyTerminalCore nativeRows ? nativeRows.Rows : core.Screen.Rows;
        int history = core.HistoryCount;
        bool forward = anchorRow < focusRow || anchorRow == focusRow && anchorCol <= focusCol;
        (int first, int from) = forward ? (anchorRow, anchorCol) : (focusRow, focusCol);
        (int last, int to) = forward ? (focusRow, focusCol) : (anchorRow, anchorCol);
        if (first < 0 || last >= history + rows) throw new ArgumentOutOfRangeException(nameof(anchorRow));
        var text = new StringBuilder();
        for (int line = first; line <= last; line++)
        {
            int begin = block ? Math.Min(anchorCol, focusCol) : line == first ? from : 0;
            int end = (block ? Math.Max(anchorCol, focusCol) : line == last ? to : cols - 1) + 1;
            int wrap = block ? -1 : core.GetRowWrapLength(line);
            bool join = line != last && wrap >= 0;
            if (join) end = Math.Min(end, wrap);
            int lineStart = text.Length;
            for (int col = Math.Max(0, begin); col < Math.Min(cols, end); col++)
            {
                Cell cell = line < history ? core.GetHistoryCell(line, col) : core.Screen[line - history, col];
                AppendCell(text, cell);
            }
            if (!join) while (text.Length > lineStart && text[^1] == ' ') text.Length--;
            if (line != last && !join) text.Append("\r\n");
        }
        return text.ToString();
    }
}
