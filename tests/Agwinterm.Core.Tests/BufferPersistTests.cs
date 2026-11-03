using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

/// <summary>Full-fidelity buffer persistence (M2 phase 3): serialize → restore preserves every
/// cell's colour and attributes (vs the old plain-text path that reseeded flat dim text).</summary>
public class BufferPersistTests
{
    internal static void VerifyPaddingOverwrite(ITerminalCore source)
    {
        source.Feed(Encoding.UTF8.GetBytes("abcd中\x1b[1;5HX"));
        Assert.True(BufferPersist.TryParse(BufferPersist.Serialize(source), out var saved));
        var restored = new TerminalEmulator(20, 4);
        BufferPersist.Restore(restored, saved);
        Assert.Equal("abcdX中", restored.DumpHistoryRow(0).TrimEnd());
    }

    internal static void VerifyResizeDoesNotSoftWrap(ITerminalCore source)
    {
        source.Feed(Encoding.ASCII.GetBytes("abcde\r\nVWXYZ\x1b[1;1H12345"));
        source.Resize(3, 4);
        Assert.True(BufferPersist.TryParse(BufferPersist.Serialize(source), out var saved));
        var restored = new TerminalEmulator(20, 4);
        BufferPersist.Restore(restored, saved);
        Assert.Equal("123", restored.DumpHistoryRow(0).TrimEnd());
        Assert.Equal("VWX", restored.DumpHistoryRow(1).TrimEnd());
    }

    [Fact]
    public void PaddingOverwriteSurvivesRestore() => VerifyPaddingOverwrite(new TerminalEmulator(5, 4));

    [Fact]
    public void ResizeDoesNotInventSoftWrap() => VerifyResizeDoesNotSoftWrap(new TerminalEmulator(5, 4));

    private static TerminalEmulator RestoreAt(TerminalEmulator source, int cols)
    {
        Assert.True(BufferPersist.TryParse(BufferPersist.Serialize(source), out var saved));
        var target = new TerminalEmulator(cols, 4);
        BufferPersist.Restore(target, saved);
        return target;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public void SoftWraps_ReflowAcrossHistoryAndScreen_WithoutJoiningHardLines(int rows)
    {
        var source = Feed(5, rows, "abcdefghijk\r\n12345\r\nend");
        var restored = RestoreAt(source, 20);
        Assert.Equal(3, restored.HistoryCount);
        Assert.Equal("abcdefghijk", restored.DumpHistoryRow(0).TrimEnd());
        Assert.Equal("12345", restored.DumpHistoryRow(1).TrimEnd());
        Assert.Equal("end", restored.DumpHistoryRow(2).TrimEnd());
        restored.Resize(4, 4);
        Assert.Equal(6, restored.HistoryCount);
        restored = RestoreAt(restored, 20);
        Assert.Equal(3, restored.HistoryCount);
        Assert.Equal("abcdefghijk", restored.DumpHistoryRow(0).TrimEnd());
        Assert.Equal("12345", restored.DumpHistoryRow(1).TrimEnd());
    }

    [Fact]
    public void Reflow_PreservesWideGlyphsAttributesAndSpaces_WithoutWrapPadding()
    {
        var link = new TerminalHyperlink("https://example.com", "id=wide");
        var source = Feed(5, 4, link.Sequence + "\x1b[31mabcd中  😀z");
        var restored = RestoreAt(source, 20);
        Assert.Equal("abcd中  😀z", restored.DumpHistoryRow(0).TrimEnd());
        Cell[] expected = Enumerable.Range(0, 12).Select(c => restored.GetHistoryCell(0, c)).ToArray();
        foreach (int width in new[] { 3, 1, 7, 5, 20 })
        {
            restored.Resize(width, 4);
            restored = RestoreAt(restored, width);
        }
        Assert.Equal(expected, Enumerable.Range(0, 12).Select(c => restored.GetHistoryCell(0, c)).ToArray());
        Assert.Equal(link, restored.GetHistoryCell(0, 4).Hyperlink);
    }

    [Fact]
    public void Restore_DoesNotJoinLegacyRowsOrNewShellOutput()
    {
        var source = Feed(5, 3, "abcde\r\nfghij");
        Assert.True(BufferPersist.TryParse(BufferPersist.Serialize(source), out var saved));
        // Missing fields in an older protobuf are false/zero.
        foreach (var row in saved.Rows) { row.SoftWrapped = false; row.WrapLength = 0; }
        var restored = new TerminalEmulator(5, 2);
        BufferPersist.Restore(restored, saved);
        restored.Feed(Encoding.ASCII.GetBytes("new\r\nshell\r\n"));
        restored.Resize(20, 2);
        Assert.Equal("abcde", restored.DumpHistoryRow(0).TrimEnd());
        Assert.Equal("fghij", restored.DumpHistoryRow(1).TrimEnd());
        Assert.Equal("new", restored.DumpHistoryRow(2).TrimEnd());
    }

    [Fact]
    public void EraseAndScroll_DoNotLeaveStaleWraps()
    {
        var source = Feed(5, 3, "abcdefghijk\x1b[2J\x1b[Hone\r\ntwo\r\nthree\r\nfour");
        var restored = RestoreAt(source, 20);
        Assert.Equal(new[] { "one", "two", "three", "four" },
            Enumerable.Range(0, restored.HistoryCount).Select(r => restored.DumpHistoryRow(r).TrimEnd()));
    }

    [Theory]
    [InlineData("\x1b[2;1H\x1b[L")]
    [InlineData("\x1b[2;1H\x1b[M")]
    [InlineData("\x1b[2;5r\x1b[2;1H\x1b[L")]
    [InlineData("\x1b[2;5r\x1b[2;1H\x1b[M")]
    public void InsertingOrDeletingLines_BreaksContinuationIntoReplacedRow(string edit)
    {
        var restored = RestoreAt(Feed(5, 5, "abcdefghijklmnop" + edit), 30);
        Assert.Equal("abcde", restored.DumpHistoryRow(0).TrimEnd());
    }

    [Fact]
    public void Reflow_RespectsHistoryLimitAndKeepsNewestRows()
    {
        var restored = new TerminalEmulator(1000, 2) { ScrollbackMax = 10 };
        restored.SeedScrollback(new[] { new string('x', 998) + "YZ" });
        restored.Resize(1, 2);
        Assert.Equal(10, restored.HistoryCount);
        Assert.Equal("Z", restored.DumpHistoryRow(9));
        restored.Resize(1000, 2);
        Assert.Equal("xxxxxxxxYZ", restored.DumpHistoryRow(0).TrimEnd());
    }

    [Fact]
    public void LegacyText_WideUnicodeSurvivesRepeatedReflow()
    {
        var restored = new TerminalEmulator(3, 3);
        restored.SeedScrollback(new[] { "ab中😀z" });
        restored.Resize(20, 3);
        Assert.Equal("ab中😀z", restored.DumpHistoryRow(0).TrimEnd());
        Assert.Equal(0, restored.GetHistoryCell(0, 3).Width);
        Assert.Equal(0x1f600, restored.GetHistoryCell(0, 4).Rune);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoundTrip_PreservesHyperlinks(bool includeVisible)
    {
        var target = new TerminalHyperlink("https://example.com/a;b", "id=docs:other=value");
        var src = Feed(12, 3, target.Sequence + "中 docs  " + TerminalHyperlink.Close + "!\r\n\r\n\r\n" +
            target.Sequence + "live" + TerminalHyperlink.Close);
        Assert.True(src.HistoryCount > 0);
        Assert.True(BufferPersist.TryParse(BufferPersist.Serialize(src, includeVisible: includeVisible), out var buffer));
        var dst = new TerminalEmulator(12, 3);
        BufferPersist.Restore(dst, buffer);
        for (int h = 0; h < src.HistoryCount; h++)
            for (int c = 0; c < 12; c++) Assert.Equal(src.GetHistoryCell(h, c), dst.GetHistoryCell(h, c));
        Assert.Equal(target, dst.GetHistoryCell(0, 1).Hyperlink); // wide spacer
        Assert.Equal(target, dst.GetHistoryCell(0, 7).Hyperlink); // linked trailing space
        Assert.Null(dst.GetHistoryCell(0, 9).Hyperlink); // punctuation after the close
        if (includeVisible)
            Assert.Equal(target, dst.GetHistoryCell(src.HistoryCount + 2, 0).Hyperlink);
        else
            Assert.Equal(src.HistoryCount, dst.HistoryCount);
    }

    [Fact]
    public void Restore_OldCellsWithoutHyperlinkFieldsRemainUnlinked()
    {
        // v1 blob written before hyperlink fields 12/13 existed: one row containing 'x'.
        byte[] oldBlob = [8, 1, 16, 8, 26, 6, 10, 4, 8, 120, 40, 1];
        Assert.True(BufferPersist.TryParse(oldBlob, out var buffer));
        var dst = new TerminalEmulator(8, 3);
        BufferPersist.Restore(dst, buffer);
        Assert.Equal('x', dst.GetHistoryCell(0, 0).Rune);
        Assert.Null(dst.GetHistoryCell(0, 0).Hyperlink);
    }

    private static TerminalEmulator Feed(int cols, int rows, string s)
    {
        var t = new TerminalEmulator(cols, rows);
        t.Feed(Encoding.UTF8.GetBytes(s));
        return t;
    }

    [Fact]
    public void RoundTrip_PreservesColorsAndAttributes()
    {
        // Colourful, styled content that scrolls into history.
        var src = Feed(40, 4,
            "\x1b[1;31mbold-red\x1b[0m plain \x1b[38;5;208morange\x1b[0m\r\n" +
            "\x1b[4;32munderline-green\x1b[0m\r\n" +
            "\x1b[7minverse\x1b[0m \x1b[3mitalic\x1b[0m\r\n" +
            "\x1b[48;2;10;20;30mtruecolor-bg\x1b[0m\r\n" +
            "more\r\n");

        byte[] blob = BufferPersist.Serialize(src);
        Assert.True(BufferPersist.TryParse(blob, out var pbuf));

        // Restore into a fresh emulator and compare the reconstructed history to the source's
        // full buffer (history + visible), cell for cell.
        var dst = new TerminalEmulator(40, 4);
        BufferPersist.Restore(dst, pbuf);

        var expected = new List<Cell[]>();
        for (int h = 0; h < src.HistoryCount; h++)
        {
            var row = new Cell[40];
            for (int c = 0; c < 40; c++) row[c] = src.GetHistoryCell(h, c);
            expected.Add(row);
        }
        for (int r = 0; r < src.Screen.Rows; r++)
        {
            var row = new Cell[40];
            for (int c = 0; c < 40; c++) row[c] = src.Screen[r, c];
            // Drop trailing all-empty rows the same way Serialize does.
            expected.Add(row);
        }
        while (expected.Count > 0 && expected[^1].All(c => c == Cell.Empty)) expected.RemoveAt(expected.Count - 1);

        Assert.Equal(expected.Count, dst.HistoryCount);
        for (int h = 0; h < expected.Count; h++)
            for (int c = 0; c < 40; c++)
                Assert.Equal(expected[h][c], dst.GetHistoryCell(h, c));
    }

    [Fact]
    public void Serialize_IsCompact_ForSparseContent()
    {
        // A mostly-blank 200-col buffer must NOT cost 200 cells/row (proto3 default omission +
        // per-row trim). One short line in a wide, tall terminal.
        var t = new TerminalEmulator(200, 50);
        t.Feed(Encoding.ASCII.GetBytes("hi\r\n"));
        byte[] blob = BufferPersist.Serialize(t);
        Assert.True(blob.Length < 200, $"sparse buffer blob unexpectedly large: {blob.Length} bytes");
    }

    [Fact]
    public void TryParse_RejectsGarbage()
    {
        Assert.False(BufferPersist.TryParse(new byte[] { 0xFF, 0xFF, 0x01, 0x02 }, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restore_NarrowThenGrow_PreservesEntireAttributedRow(bool saveWhileNarrow)
    {
        var link = new TerminalHyperlink("https://example.com", "id=resize");
        var wide = Feed(120, 3, link.Sequence + "\x1b[31m" + new string('x', 118) + "中" + TerminalHyperlink.Close);
        Assert.True(BufferPersist.TryParse(BufferPersist.Serialize(wide), out var saved));
        var narrow = new TerminalEmulator(40, 3);
        BufferPersist.Restore(narrow, saved);
        if (saveWhileNarrow)
        {
            Assert.True(BufferPersist.TryParse(BufferPersist.Serialize(narrow), out saved));
            narrow = new TerminalEmulator(40, 3);
            BufferPersist.Restore(narrow, saved);
        }
        narrow.Resize(120, 3);
        for (int c = 0; c < 120; c++)
            Assert.Equal(wide.Screen[0, c], narrow.GetHistoryCell(0, c));
    }

    [Fact]
    public void LegacyTextRestore_NarrowThenGrow_PreservesEntireLine()
    {
        var terminal = new TerminalEmulator(5, 3);
        terminal.SeedScrollback(new[] { "0123456789" });
        terminal.Resize(10, 3);
        for (int c = 0; c < 10; c++)
            Assert.Equal('0' + c, terminal.GetHistoryCell(0, c).Rune);
    }
}
