using Xunit;

namespace Agwinterm.Core.Tests;

public class WebNavigationPolicyTests
{
    [Theory]
    [InlineData("https://other.test/", false, false, false, true)]
    [InlineData("https://other.test/", false, false, true, false)]
    [InlineData("https://other.test/", true, false, false, false)]
    [InlineData("https://page.test/asset.html", true, false, false, true)]
    [InlineData("https://page.test/asset.html", true, true, false, false)]
    [InlineData("about:blank", false, false, true, true)]
    [InlineData("about:blank", true, false, false, false)]
    [InlineData("about:srcdoc", true, false, false, false)]
    [InlineData("file:///C:/secret", false, false, false, false)]
    public void FramePolicyPreservesLocalReviewAndChromelessBoundaries(string destination, bool local, bool review, bool chromeless, bool allowed)
        => Assert.Equal(allowed, WebNavigationPolicy.AllowsFrame("https://page.test/", destination, local, review, chromeless));

    [Theory]
    [InlineData("https://www.google.com/", true)]
    [InlineData("about:blank", true)]
    [InlineData("https://accounts.google.com/login", true)]
    [InlineData("https://example.org/", true)]
    [InlineData("http://localhost:8000/", true)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,hello", false)]
    [InlineData("ms-settings:display", false)]
    [InlineData("https://user:password@example.org/", false)]
    public void RemoteNavigationKeepsWebSchemesWithoutOriginPin(string destination, bool allowed)
        => Assert.Equal(allowed, WebNavigationPolicy.Allows("https://google.com/", destination, false, false, browse: true));

    [Theory]
    [InlineData("https://google.com/search?q=test", true)]
    [InlineData("about:blank", true)]
    [InlineData("https://GOOGLE.com:443/", true)]
    [InlineData("https://www.google.com/", false)]
    [InlineData("http://google.com/", false)]
    [InlineData("https://google.com:444/", false)]
    public void DefaultRemoteModePinsOriginalOrigin(string destination, bool allowed)
        => Assert.Equal(allowed, WebNavigationPolicy.Allows("https://google.com/", destination, false, false));

    [Theory]
    [InlineData("https://page.invalid/doc.html", false, true)]
    [InlineData("https://page.invalid/asset.html", false, true)]
    [InlineData("https://example.org/", false, false)]
    [InlineData("file:///C:/secret", false, false)]
    [InlineData("https://page.invalid/doc.html#note", true, true)]
    [InlineData("https://page.invalid/asset.html", true, false)]
    [InlineData("https://example.org/", true, false)]
    public void LocalAndReviewGrantsRemainBounded(string destination, bool review, bool allowed)
        => Assert.Equal(allowed, WebNavigationPolicy.Allows("https://page.invalid/doc.html", destination, true, review));
}
