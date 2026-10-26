using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class HudMarkdownTests
{
    [Fact] public void NativeSubsetHasExplicitStylesAndNoExecutableMarkup()
    {
        var d = HudMarkdown.Parse("# Заголовок\n**bold** and *italic* and `code`\n- list\n> quote\n---\n```sh\necho $HOME\n```\n<b>literal</b>");
        Assert.Contains("Заголовок", d.Text); Assert.Contains("• list", d.Text); Assert.Contains("│ quote", d.Text);
        Assert.Contains("echo $HOME", d.Text); Assert.Contains("<b>literal</b>", d.Text);
        Assert.Contains(d.Ranges, r => r.Size == 1.5f && r.Bold);
        Assert.Contains(d.Ranges, r => r.Italic); Assert.Contains(d.Ranges, r => r.Code);
        Assert.All(d.Ranges, r => Assert.InRange(r.Start + r.Length, 0, d.Text.Length));
    }
    [Fact] public void TablesAreBoundedAndHeaderIsDistinct()
    {
        var d = HudMarkdown.Parse("| A | Б |\n| --- | --- |\n| one | два |");
        Assert.Equal("A   │ Б\none │ два", d.Text); Assert.Contains(d.Ranges, r => r.Bold && r.Code);
        var columns = string.Join('|', Enumerable.Repeat("a", 9));
        var divider = string.Join('|', Enumerable.Repeat("---", 9));
        Assert.Throws<ArgumentException>(() => HudMarkdown.Parse(columns + "\n" + divider));
        Assert.Throws<ArgumentException>(() => HudMarkdown.Parse("a|b\n---|---\n" + string.Join('\n', Enumerable.Repeat("x|y", 32))));
    }
    [Theory][InlineData("bad\u001btext")][InlineData("bad\0text")]
    public void ControlsRefuse(string text) => Assert.Throws<ArgumentException>(() => HudMarkdown.Parse(text));
    [Theory]
    [InlineData("A\tB|C\n---|---\n12345|D", "A    B │ C\n12345  │ D")]
    [InlineData("A|C\n---|---\n1\t2|D", "A      │ C\n1    2 │ D")]
    public void TableTabsAreExpandedBeforeColumnAlignment(string markdown, string expected)
    {
        var document = HudMarkdown.Parse(markdown);
        Assert.Equal(expected, document.Text);
        var rows = document.Text.Split('\n');
        Assert.Equal(rows[0].IndexOf('│'), rows[1].IndexOf('│'));
    }
    [Fact] public void WorkBoundsAndMalformedMarkupArePredictable()
    {
        Assert.Throws<ArgumentException>(() => HudMarkdown.Parse(new string('x', 4097)));
        Assert.Throws<ArgumentException>(() => HudMarkdown.Parse(string.Join('\n', Enumerable.Repeat("x", 129))));
        Assert.Equal("*unclosed <tag> [link](url)", HudMarkdown.Parse("*unclosed <tag> [link](url)").Text);
        Assert.Equal("é", HudMarkdown.Parse("e\u0301").Text);
    }
}
