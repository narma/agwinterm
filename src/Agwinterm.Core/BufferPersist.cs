using Agwinterm.Core.Persist;
using Google.Protobuf;

namespace Agwinterm.Core;

/// <summary>
/// Full-fidelity buffer persistence (M2 phase 3): serialize the scrollback + visible grid to a
/// protobuf blob that restores every cell's colour and attributes, and reseed from it. Replaces the
/// plain-text reopen/restore path (which reseeded flat dim text). Blob is per-cell but compact —
/// proto3 omits default fields, so a run of plain default cells costs almost nothing, and rows are
/// trimmed to their content (reads pad with <see cref="Cell.Empty"/>).
/// </summary>
public static class BufferPersist
{
    public const uint Version = 1;

    /// <summary>Import legacy plain rows as inert data, without a parser or live terminal.</summary>
    public static byte[] FromPlainText(IEnumerable<string> lines,int columns)
    {
        var buffer=new PBuffer { Version=Version,Cols=(uint)Math.Clamp(columns,1,32768) };
        foreach(string line in lines.TakeLast(500))
        {
            var row=new PRow();
            foreach(var rune in line.EnumerateRunes().Take(32768))
                row.Cells.Add(new PCell { Rune=System.Text.Rune.IsControl(rune) ? 32 : rune.Value,Width=1 });
            buffer.Rows.Add(row);
        }
        return buffer.ToByteArray();
    }

    private static uint Pack(Color c) => ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
    private static Color Unpack(uint v) => new((byte)(v >> 16), (byte)(v >> 8), (byte)v);

    private static PCell ToP(Cell c) => new()
    {
        Rune = c.Rune,
        Fg = Pack(c.Foreground),
        Bg = Pack(c.Background),
        Attrs = (uint)c.Attributes,
        Width = c.Width,
        FgKind = (uint)c.FgSpec.Kind,
        FgIndex = c.FgSpec.Index,
        FgRgb = Pack(c.FgSpec.Rgb),
        BgKind = (uint)c.BgSpec.Kind,
        BgIndex = c.BgSpec.Index,
        BgRgb = Pack(c.BgSpec.Rgb),
        HyperlinkUri = c.Hyperlink?.Uri ?? "",
        HyperlinkParameters = c.Hyperlink?.Parameters ?? "",
        Grapheme = c.Grapheme ?? "",
        UnderlineStyle = (uint)c.UnderlineStyle,
        UnderlineColor = Pack(c.UnderlineColor ?? default),
        UnderlineKind = (uint)c.UnderlineSpec.Kind,
        UnderlineIndex = c.UnderlineSpec.Index,
        UnderlineRgb = Pack(c.UnderlineSpec.Rgb),
    };

    internal static Cell FromP(PCell p) => new(
        p.Rune, Unpack(p.Fg), Unpack(p.Bg), (CellAttributes)p.Attrs, (byte)p.Width,
        new ColorSpec((ColorSpecKind)p.FgKind, (byte)p.FgIndex, Unpack(p.FgRgb)),
        new ColorSpec((ColorSpecKind)p.BgKind, (byte)p.BgIndex, Unpack(p.BgRgb)),
        p.HyperlinkUri.Length == 0 ? null : new TerminalHyperlink(p.HyperlinkUri, p.HyperlinkParameters),
        p.Grapheme.Length == 0 ? null : p.Grapheme, (UnderlineStyle)p.UnderlineStyle,
        p.UnderlineKind == 0 ? null : Unpack(p.UnderlineColor),
        new ColorSpec((ColorSpecKind)p.UnderlineKind, (byte)p.UnderlineIndex, Unpack(p.UnderlineRgb)));

    private static bool IsEmpty(Cell cell) => cell.Rune is 0 or 32 && cell.Grapheme is null && cell.Hyperlink is null
        && cell.Attributes == CellAttributes.None && cell.FgSpec.Kind == ColorSpecKind.Default && cell.BgSpec.Kind == ColorSpecKind.Default;

    /// <summary>Capture buffer rows as an attributed blob. <paramref name="includeVisible"/> controls
    /// whether the live grid is appended after the scrollback (true for full save/restore; false for
    /// pty-host reattach, where the live screen arrives separately via a ConPTY repaint and only the
    /// history should be seeded). Trailing empty cells per row are dropped; it round-trips exactly.</summary>
    public static byte[] Serialize(ITerminalCore core, int maxRows = 500, bool includeVisible = true)
    {
        var emu = core;
        maxRows = Math.Clamp(maxRows, 0, 100000);
        var buf = new PBuffer { Version = Version, Cols = (uint)emu.Screen.Cols };
        int cols = emu.Screen.Cols;

        var rows = new List<PRow>();
        void AddRow(int width, int wrapLength, Func<int, Cell> cellAt)
        {
            int len = width;
            while (len > 0 && IsEmpty(cellAt(len - 1))) len--;   // native empty cells use rune 0
            var pr = new PRow { SoftWrapped = wrapLength >= 0, WrapLength = (uint)Math.Max(0, wrapLength) };
            for (int c = 0; c < len; c++) pr.Cells.Add(ToP(cellAt(c)));
            rows.Add(pr);
        }

        for (int h = Math.Max(0, emu.HistoryCount - maxRows); h < emu.HistoryCount; h++)
        {
            int hi = h;
            AddRow(emu.GetHistoryRowLength(hi),
                emu.GetRowWrapLength(hi), c => emu.GetHistoryCell(hi, c));
        }
        if (includeVisible)
            for (int r = 0; r < emu.Screen.Rows; r++)
            {
                int rr = r;
                AddRow(cols, emu.Screen.WrapLengths[rr], c => emu.Screen[rr, c]);
            }
        // Drop trailing all-empty rows, then cap to maxRows (keep the newest).
        while (rows.Count > 0 && rows[^1].Cells.Count == 0) rows.RemoveAt(rows.Count - 1);
        if (rows.Count > maxRows) rows.RemoveRange(0, rows.Count - maxRows);
        buf.Rows.AddRange(rows);
        return buf.ToByteArray();
    }

    /// <summary>Try to parse a persisted blob; false if it isn't a v1 <see cref="PBuffer"/>.</summary>
    public static bool TryParse(byte[] blob, out PBuffer buffer)
    {
        buffer = new PBuffer();
        if (blob.Length > 64 * 1024 * 1024) return false;
        try
        {
            buffer = PBuffer.Parser.ParseFrom(blob);
            return buffer.Version == Version;
        }
        catch (InvalidProtocolBufferException) { return false; }
    }

    /// <summary>Restore attributed logical lines at the current width. Missing wrap metadata
    /// in older blobs means a hard line boundary; guessing would merge unrelated output.</summary>
    public static void Restore(ITerminalCore core, PBuffer buffer)
    {
        core.RestoreHistory(buffer);
    }
}
