using System.Text;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class BufferPersistTests
{
    [Fact]
    public void GhosttyHistoryRestorePreservesTextAttributesAndHyperlinks()
    {
        using var source = new GhosttyTerminalCore(20, 4);
        var link = new TerminalHyperlink("https://example.com", "id=restore");
        source.Feed(Encoding.UTF8.GetBytes(link.Sequence + "\x1b[31;1mred" +
            TerminalHyperlink.Close + "\x1b[0m  text\r\nnext"));
        Assert.True(BufferPersist.TryParse(BufferPersist.Serialize(source), out var saved));

        using var restored = new GhosttyTerminalCore(20, 4);
        BufferPersist.Restore(restored, saved);
        Assert.Equal("red  text", restored.DumpHistoryRow(0));
        Assert.Equal("next", restored.DumpHistoryRow(1));
        Assert.Equal(source.Screen[0, 0].FgSpec, restored.GetHistoryCell(0, 0).FgSpec);
        Assert.Equal(source.Screen[0, 0].Attributes, restored.GetHistoryCell(0, 0).Attributes);
        Assert.Equal(link.Uri, restored.GetHistoryCell(0, 0).Hyperlink?.Uri);
        Assert.Equal(source.Screen[0, 0].Hyperlink, restored.GetHistoryCell(0, 0).Hyperlink);
    }

    [Fact]
    public void TryParseRejectsGarbage()
        => Assert.False(BufferPersist.TryParse(new byte[] { 0xff, 0xff, 0x01, 0x02 }, out _));
}
