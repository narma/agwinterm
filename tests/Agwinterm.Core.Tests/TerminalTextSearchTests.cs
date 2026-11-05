using System.Text;
using Agwinterm.Core;
using Xunit;

namespace Agwinterm.Core.Tests;

public class TerminalTextSearchTests
{
    private static Cell[] Cells(string text) => text.EnumerateRunes()
        .Select(r => Cell.Empty with { Rune = r.Value }).ToArray();

    [Theory]
    [InlineData("ПРИВЕТ мир", "привет", 0, 5)]
    [InlineData("Σ σ ς", "Σ", 0, 0)]
    [InlineData("𐐀hello", "𐐨HELLO", 0, 5)]
    public void UsesInvariantUnicodeScalars(string text, string query, int start, int end)
    {
        var search = new TerminalTextSearch(query);
        search.AddRow(7, Cells(text), -1);
        Assert.Equal((7, start, 7, end), search.Matches[0]);
        if (query == "Σ") Assert.Equal(3, search.Matches.Count);
    }

    [Fact]
    public void SoftWrapsJoinButHardBreaksDoNot()
    {
        var search = new TerminalTextSearch("привет");
        search.AddRow(2, Cells("ПРИ"), 3);
        search.AddRow(3, Cells("ВЕТ"), -1);
        search.AddRow(4, Cells("ПРИ"), -1);
        search.AddRow(5, Cells("ВЕТ"), -1);
        Assert.Equal((2, 0, 3, 2), Assert.Single(search.Matches));
    }

    [Fact]
    public void ClusterAndWidePaddingRetainCellEndpoints()
    {
        Cell[] row = [Cell.Empty with { Rune = 'e', Grapheme = "é" },
            Cell.Empty with { Rune = 0x1f469, Grapheme = "👩🏽‍💻", Width = 2 },
            Cell.Empty with { Width = 0 }, Cell.Empty with { Rune = 'Ж' }];
        var search = new TerminalTextSearch("́👩🏽‍💻ж");
        search.AddRow(0, row, -1);
        Assert.Equal((0, 0, 0, 3), Assert.Single(search.Matches));
        var wide = new TerminalTextSearch("👩");
        wide.AddRow(0, row, -1);
        Assert.Equal((0, 1, 0, 2), Assert.Single(wide.Matches));
    }

    [Fact]
    public void OverlapsAndRepeatedPrefixesUseLinearStreamingMatch()
    {
        var search = new TerminalTextSearch("aaab");
        search.AddRow(0, Cells(new string('a', 10000) + "baaab"), -1);
        Assert.Equal(2, search.Matches.Count);
        var overlaps = new TerminalTextSearch("aaa");
        overlaps.AddRow(0, Cells("aaaa"), -1);
        Assert.Equal(2, overlaps.Matches.Count);
    }

    [Fact]
    public void BoundedResultsAndCancellation()
    {
        var search = new TerminalTextSearch("x");
        var row = Cells(new string('x', 200));
        for (int i = 0; i <= 500; i++) search.AddRow(i, row, -1);
        Assert.Equal(TerminalTextSearch.MatchLimit, search.Matches.Count);
        Assert.True(search.Truncated);
        var cancelled = new TerminalTextSearch("y");
        Assert.Throws<OperationCanceledException>(() => cancelled.AddRow(0, row, -1, new CancellationToken(true)));
    }

    [Fact]
    public void DoesNotInventNormalizationOrExpansions()
    {
        var search = new TerminalTextSearch("ss");
        search.AddRow(0, Cells("ß"), -1);
        Assert.Empty(search.Matches);
        var normalized = new TerminalTextSearch("é");
        normalized.AddRow(0, [Cell.Empty with { Grapheme = "é" }], -1);
        Assert.Empty(normalized.Matches);
    }
}
