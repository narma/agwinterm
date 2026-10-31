using Xunit;

namespace Agwinterm.Core.Tests;

public class WebChromeLayoutTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void NarrowNavigationReservesReadableIdentity(double scale)
    {
        int width = (int)(320 * scale);
        var layout = WebChromeLayout.Fit(width, scale, true, false);
        Assert.True(layout.NavigationCount < 5);
        Assert.True(layout.TitleRight - layout.TitleLeft >= 96 * scale);
        Assert.True(layout.TitleLeft >= layout.NavigationCount * layout.ButtonWidth);
        Assert.True(layout.TitleRight <= width - 2 * layout.ButtonWidth);
        Assert.Equal(width - layout.TitleRight, layout.TitleLeft);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(160)]
    [InlineData(200)]
    public void SmallPagesYieldButtonsBeforeIdentity(int width)
    {
        var layout = WebChromeLayout.Fit(width, 1, true, false);
        Assert.Equal(0, layout.NavigationCount);
        Assert.True(layout.TitleRight - layout.TitleLeft >= 48);
        Assert.True(layout.TitleRight <= width - layout.ButtonWidth * (layout.ShowHide ? 2 : 1));
    }
}
