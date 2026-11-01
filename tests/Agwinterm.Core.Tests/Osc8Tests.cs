using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class Osc8Tests
{
    [Fact]
    public void ManagedCore_HyperlinksFollowCells() => Verify(new TerminalEmulator(8, 3));

    [Fact]
    public void ManagedCore_WideEditsClearTheWholeGlyph() => VerifyWideEdits(new TerminalEmulator(8, 3));

    internal static void VerifyWideEdits(ITerminalCore t)
    {
        var target = new TerminalHyperlink("https://example.com", "id=wide");
        foreach (var (edit, cleared) in new (string, int[])[]
        {
            ("\rX", [0, 1]),
            ("\x1b[2GX", [0, 1]),
            ("\x1b[2G界", [0, 1, 2, 3]), // intersects both wide glyphs
            ("\r\x1b[X", [0, 1]),
            ("\x1b[2G\x1b[X", [0, 1]),
            ("\x1b[2G\x1b[K", [0, 1, 2, 3]),
            ("\x1b[3G\x1b[1K", [0, 1, 2, 3]),
        })
        {
            t.Feed(Encoding.UTF8.GetBytes("\x1b[H\x1b[2J" + target.Sequence + "中中" + TerminalHyperlink.Close + edit));
            for (int c = 0; c < 4; c++)
                Assert.Equal(cleared.Contains(c) ? null : target, t.Screen[0, c].Hyperlink);
            for (int c = 0; c < 8; c++)
            {
                var cell = t.Screen[0, c];
                if (cell.Width == 0) Assert.True(c > 0 && t.Screen[0, c - 1].Width == 2);
                if (cell.Width == 2) Assert.True(c < 7 && t.Screen[0, c + 1].Width == 0);
            }
        }
        // Wrapping a new wide glyph from the last column clears the old glyph there.
        t.Feed(Encoding.UTF8.GetBytes("\x1b[H\x1b[2J\x1b[7G" + target.Sequence + "中" + TerminalHyperlink.Close + "\x1b[8G界"));
        Assert.Equal(Cell.Empty, t.Screen[0, 6]);
        Assert.Equal(Cell.Empty, t.Screen[0, 7]);
    }

    internal static void Verify(ITerminalCore t)
    {
        void Feed(string text) => t.Feed(Encoding.UTF8.GetBytes(text));
        var target = new TerminalHyperlink("https://example.com/a;b", "id=docs:other=value");
        // A sequence can arrive one byte at a time; BEL and ST both terminate OSC.
        foreach (byte b in Encoding.UTF8.GetBytes("\x1b]8;id=docs:other=value;https://example.com/a;b\x07"))
            t.Feed(new[] { b });
        Feed("A\x1b[0m中B");
        for (int c = 0; c < 4; c++) Assert.Equal(target, t.Screen[0, c].Hyperlink);
        Feed(TerminalHyperlink.Close + "x");
        Assert.Null(t.Screen[0, 4].Hyperlink);
        Feed("\r\n\r\n\r\n");
        Assert.Equal(target, t.GetHistoryCell(0, 0).Hyperlink);
        Assert.Null(t.GetHistoryCell(0, 4).Hyperlink);
        t.Resize(10, 3);
        Assert.Equal(target, t.GetHistoryCell(0, 3).Hyperlink);

        Feed("\x1b[H" + target.Sequence + "1234567890Z" + TerminalHyperlink.Close);
        Assert.Equal(target, t.Screen[1, 0].Hyperlink); // wrapped label
        Feed("\x1b[H!");
        Assert.Null(t.Screen[0, 0].Hyperlink); // overwrite clears the target
        Feed("\x1b[1;2H\x1b[X");
        Assert.Null(t.Screen[0, 1].Hyperlink); // erasing never leaves a clickable blank
        Feed("\x1b[?1049h" + target.Sequence + "alt" + TerminalHyperlink.Close);
        Assert.Equal(target, t.Screen[0, 0].Hyperlink);
        Feed("\x1b[?1049l");
        Assert.Null(t.Screen[0, 0].Hyperlink);
        Assert.Equal(target, t.Screen[0, 2].Hyperlink);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DumpScreen_PreservesLinksAndTheActiveTarget(bool close)
    {
        var source = new TerminalEmulator(6, 3);
        source.Feed(Encoding.UTF8.GetBytes("\x1b]8;id=x;https://example.com\x1b\\abcdef" +
            (close ? TerminalHyperlink.Close : "")));
        var replay = new TerminalEmulator(6, 3);
        replay.Feed(Encoding.UTF8.GetBytes(source.DumpScreen()));
        foreach (var t in new[] { source, replay }) t.Feed(Encoding.UTF8.GetBytes("z"));
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 6; c++) Assert.Equal(source.Screen[r, c], replay.Screen[r, c]);
    }

    [Fact]
    public void MalformedOsc8_DoesNotChangeTheTarget()
    {
        var t = new TerminalEmulator(8, 3);
        var host = new RecordingHost(); t.Host = host;
        t.Feed(Encoding.UTF8.GetBytes("\x1b]8;;https://example.com\x07\x1b]8;malformed\x07x\x1b]8;;\x07y"));
        Assert.Equal("https://example.com", t.Screen[0, 0].Hyperlink?.Uri);
        Assert.Null(t.Screen[0, 1].Hyperlink);
        Assert.Empty(host.Unhandleds);
    }
}
