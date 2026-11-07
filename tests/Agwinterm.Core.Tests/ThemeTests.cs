using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class ThemeTests
{
    [Fact]
    public void EmptyCell_SpecsAreDefault()
    {
        Assert.Equal(ColorSpec.Default, Cell.Empty.FgSpec);
        Assert.Equal(ColorSpec.Default, Cell.Empty.BgSpec);
    }

    [Fact]
    public void DefaultTheme_ResolvesToLegacyRgb()
    {
        var th = Theme.Default;
        // Resolving a cell's spec through the default theme must equal its baked RGB (Foreground/Background).
        var cell = Cell.Empty with { Foreground = Color.FromIndex(1), Background = Color.FromIndex(4),
            FgSpec = ColorSpec.Indexed(1), BgSpec = ColorSpec.Indexed(4) };
        Assert.Equal(cell.Foreground, th.ResolveFg(cell.FgSpec));
        Assert.Equal(cell.Background, th.ResolveBg(cell.BgSpec));
        Assert.Equal(Color.FromIndex(1), th.ResolveFg(ColorSpec.Indexed(1)));
        Assert.Equal(Color.DefaultForeground, th.ResolveFg(ColorSpec.Default));
        Assert.Equal(Color.DefaultBackground, th.ResolveBg(ColorSpec.Default));
        Assert.Equal(new Color(1, 2, 3), th.ResolveFg(ColorSpec.FromRgb(new Color(1, 2, 3))));
        Assert.Equal(Color.FromIndex(200), th.ResolveFg(ColorSpec.Indexed(200))); // 16-255 unthemed
    }

    [Fact]
    public void CustomTheme_RemapsIndexedAndDefault()
    {
        var pal = Theme.DefaultPalette();
        pal[1] = new Color(1, 1, 1); // remap "red"
        var th = new Theme { Palette = pal, DefaultBackground = new Color(250, 250, 250) };
        Assert.Equal(new Color(1, 1, 1), th.ResolveFg(ColorSpec.Indexed(1)));
        Assert.Equal(new Color(250, 250, 250), th.ResolveBg(ColorSpec.Default));
    }
}
