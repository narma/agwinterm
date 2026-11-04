using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class TerminalTextLineTests
{
    [Fact]
    public void EmojiCombiningAndWideCellsMapBothDirections()
    {
        var line = new TerminalTextLine([
            Cell.Empty with { Rune = 'A' },
            Cell.Empty with { Rune = 0x1f469, Grapheme = "👩🏽‍💻", Width = 2 },
            Cell.Empty with { Width = 0 },
            Cell.Empty with { Rune = 'e', Grapheme = "e\u0301" },
            Cell.Empty with { Rune = 'Z' }]);
        Assert.Equal("A👩🏽‍💻e\u0301Z", line.Text);
        Assert.Equal(1, line.OffsetAtColumn(2)); // wide trailing cell hits its cluster
        Assert.Equal(8, line.OffsetAtColumn(3));
        Assert.Equal(10, line.OffsetAtColumn(4));
        Assert.Equal(1, line.ColumnAtOffset(4)); // inside emoji UTF-16 sequence
        Assert.Equal(3, line.ColumnAtOffset(4, end: true));
        Assert.Equal(3, line.ColumnAtOffset(9)); // combining mark covers the same cell
        Assert.Equal(4, line.ColumnAtOffset(10, end: true));
        Assert.Equal(5, line.ColumnAtOffset(line.Text.Length));
    }
}
