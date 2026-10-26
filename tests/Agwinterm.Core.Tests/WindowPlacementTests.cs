namespace Agwinterm.Core.Tests;

public class WindowPlacementTests
{
    [Fact] public void NegativeWorkAreaClampsWithoutDiscardingItsOrigin()
    {
        Assert.Equal(new DesktopRect(-810, 40, 800, 600), WindowPlacement.Fit(new(100, -100, 800, 600), new(-1610, 40, 1600, 860)));
    }
    [Fact] public void OversizedWindowFitsWithinWorkArea()
    {
        Assert.Equal(new DesktopRect(-1200, -800, 1200, 760), WindowPlacement.Fit(new(300, 500, 2000, 1800), new(-1200, -800, 1200, 760)));
    }
    [Fact] public void ExistingContainedRectangleIsPreserved()
    {
        var rect = new DesktopRect(100, 120, 700, 500);
        Assert.Equal(rect, WindowPlacement.Fit(rect, new(0, 40, 1920, 1040)));
    }
}
