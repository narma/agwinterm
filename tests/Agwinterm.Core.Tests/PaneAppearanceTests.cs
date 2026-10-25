namespace Agwinterm.Core.Tests;

public class PaneAppearanceTests
{
    [Theory]
    [InlineData("#12345")][InlineData("#12345Z")][InlineData("red")]
    public void BadColorsCannotReachRendering(string color) => Assert.Throws<ArgumentException>(() => new PaneAppearance(Color:color).Validate());
    [Fact] public void TextBoundsCountUnicodeScalarsAndLines()
    {
        new PaneAppearance(Text:string.Concat(Enumerable.Repeat("😀",512))).Validate();
        Assert.Throws<ArgumentException>(() => new PaneAppearance(Text:new string('x',513)).Validate());
        Assert.Throws<ArgumentException>(() => new PaneAppearance(Text:new string('\n',12)).Validate());
        Assert.Throws<ArgumentException>(() => new PaneAppearance(Text:"x\u001by").Validate());
        Assert.Null(PaneAppearance.Restore(new(StatusShape:"star")));
    }
    [Theory]
    [InlineData(false,true,"Light","Dark","Manual")]
    [InlineData(true,true,"Light","Dark","Light")]
    [InlineData(true,false,"Light","Dark","Dark")]
    [InlineData(true,true,"","Dark","Manual")]
    [InlineData(true,false,"Light","","Manual")]
    public void SystemThemeHasDeterministicFallback(bool follow,bool light,string lightTheme,string darkTheme,string expected) =>
        Assert.Equal(expected,SystemThemeChoice.Select("Manual",follow,light,lightTheme,darkTheme));
}
