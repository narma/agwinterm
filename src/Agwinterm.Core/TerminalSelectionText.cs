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
    public static string Read(ITerminalCore core, int anchorRow, int anchorCol, int focusRow, int focusCol, bool block,
        int maximumCharacters=int.MaxValue)
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
                Cell cell = core is PresentedTerminalView presented ? presented.GetCachedCell(line,col)
                    : line < history ? core.GetHistoryCell(line, col) : core.Screen[line - history, col];
                int characters=cell.Width == 0 ? 0 : cell.Grapheme?.Length ?? (cell.Rune > 0xffff ? 2 : 1);
                if(characters > maximumCharacters-text.Length) throw new InvalidDataException("Selection exceeds the copy limit.");
                AppendCell(text, cell);
            }
            if (!join) while (text.Length > lineStart && text[^1] == ' ') text.Length--;
            if (line != last && !join)
            {
                if(maximumCharacters-text.Length < 2) throw new InvalidDataException("Selection exceeds the copy limit.");
                text.Append("\r\n");
            }
        }
        return text.ToString();
    }
}
