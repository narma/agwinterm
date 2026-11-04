using System.Text;
using Agwinterm.Core.Persist;

namespace Agwinterm.Core;

/// <summary>Builds a bounded, inert attributed import for a separate native terminal.
/// These bytes never enter the destination's live parser or execute host effects.</summary>
internal static class GhosttyHistory
{
    public static byte[] Encode(PBuffer buffer)
    {
        if (buffer.Version != BufferPersist.Version || buffer.Cols is 0 or > 32768 || buffer.Rows.Count > 100000)
            throw new InvalidDataException("Invalid history dimensions.");
        var text = new StringBuilder("\u001b[?2027h");
        foreach (var row in buffer.Rows)
        {
            if (row.Cells.Count > 32768 || row.WrapLength > buffer.Cols)
                throw new InvalidDataException("Invalid history row.");
            int columns = row.SoftWrapped ? (int)row.WrapLength : row.Cells.Count;
            for (int col = 0; col < columns; col++)
            {
                PCell? cell = col < row.Cells.Count ? row.Cells[col] : null;
                if (cell is { Width: 0 }) continue;
                text.Append("\u001b[0");
                if (cell is not null)
                {
                    foreach (var (flag, code) in Styles) if ((cell.Attrs & (uint)flag) != 0) text.Append(';').Append(code);
                    Color(text, cell.FgKind, cell.FgIndex, cell.FgRgb, 38);
                    Color(text, cell.BgKind, cell.BgIndex, cell.BgRgb, 48);
                }
                text.Append('m');
                bool link = cell is { HyperlinkUri.Length: > 0 };
                if (link) { text.Append("\u001b]8;;"); Plain(text, cell!.HyperlinkUri); text.Append("\u001b\\"); }
                if (cell is null) text.Append(' ');
                else if (cell.Grapheme.Length > 0) Plain(text, cell.Grapheme);
                else if (Rune.TryCreate(cell.Rune, out var rune) && !Rune.IsControl(rune)) text.Append(rune.ToString());
                else text.Append(' ');
                if (link) text.Append("\u001b]8;;\u001b\\");
                if (text.Length > 16 * 1024 * 1024) throw new InvalidDataException("History import exceeds the size limit.");
            }
            if (!row.SoftWrapped) text.Append("\r\n");
        }
        // Even a truncated logical line must leave all imported content in history.
        if (buffer.Rows.Count > 0 && buffer.Rows[^1].SoftWrapped) text.Append("\r\n");
        return Encoding.UTF8.GetBytes(text.ToString());
    }
    private static readonly (CellAttributes Flag, int Code)[] Styles =
        [(CellAttributes.Bold, 1), (CellAttributes.Dim, 2), (CellAttributes.Italic, 3),
         (CellAttributes.Underline, 4), (CellAttributes.Inverse, 7), (CellAttributes.Strikethrough, 9)];
    private static void Plain(StringBuilder text, string value)
    {
        if (value.Length > 65536) throw new InvalidDataException("History string exceeds the size limit.");
        foreach (var rune in value.EnumerateRunes()) text.Append(Rune.IsControl(rune) ? " " : rune.ToString());
    }
    private static void Color(StringBuilder text, uint kind, uint index, uint rgb, int code)
    {
        if (kind == (uint)ColorSpecKind.Indexed) text.Append(';').Append(code).Append(";5;").Append(Math.Min(index, 255));
        else if (kind == (uint)ColorSpecKind.Rgb) text.Append(';').Append(code).Append(";2;")
            .Append((rgb >> 16) & 255).Append(';').Append((rgb >> 8) & 255).Append(';').Append(rgb & 255);
    }
}
