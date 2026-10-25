using Xunit;

namespace Agwinterm.Core.Tests;

public class OverlayGeometryTests
{
    [Theory]
    [InlineData(96, 400, 360)]
    [InlineData(144, 600, 360)]
    [InlineData(192, 800, 360)]
    public void MixedAxesUseDipAndPercent(float dpi, float width, float height)
    {
        var box = new OverlayGeometry(60, 400).Fit(20, 30, 1000, 600, dpi);
        Assert.Equal(width, box.w); Assert.Equal(height, box.h);
        Assert.Equal(20 + (1000-width)/2, box.x); Assert.Equal(30 + (600-height)/2, box.y);
    }

    [Fact]
    public void PartialResizePercentResetAndFullHaveDistinctMeanings()
    {
        var request = new OverlayGeometry(60, 400).Resize(null, null, 300, false);
        Assert.Equal(new OverlayGeometry(60, 400, 300), request);
        Assert.Equal(new OverlayGeometry(70, null, 200), request.Resize(70, null, 200, false));
        Assert.Equal(new OverlayGeometry(), request.Resize(null, null, null, true));
        Assert.Equal((0f, 0f, 12f, 7f), request.Fit(0, 0, 12, 7, 192));
        Assert.Equal(400, request.WidthDip);
        Assert.Equal((2f, 3f, 0f, 0f), request.Fit(2, 3, -1, 0, 96));
    }
}
