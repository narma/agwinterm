using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class ThemeColorsTests
{
    [Fact]
    public void ThemeColors_FormatAndParseRoundTrip_MalformedIsNull()
    {
        var theme = (new Color(0xcc, 0xcc, 0xcc), new Color(0x12, 0x34, 0x56));
        Assert.Equal("cccccc;123456", ThemeColors.Format(theme.Item1, theme.Item2));
        Assert.Equal(theme, ThemeColors.Parse("cccccc;123456"));
        Assert.Null(ThemeColors.Parse(null));
        Assert.Null(ThemeColors.Parse("cccccc"));
        Assert.Null(ThemeColors.Parse("cccccc;12345g"));
        var palette = Theme.DefaultPalette();
        Assert.Equal(palette, ThemeColors.ParsePalette(ThemeColors.FormatPalette(palette)));
        Assert.Null(ThemeColors.ParsePalette("112233"));
        Assert.Null(ThemeColors.ParsePalette(new string('a', 2000)));
        Assert.Null(ThemeColors.ParsePalette(string.Join(';', Enumerable.Repeat("ffffff", 15).Append("zzzzzz"))));
        Assert.Equal(new Color(1, 2, 3), ThemeColors.ParseColor(ThemeColors.FormatColor(new Color(1, 2, 3))));
    }

}
