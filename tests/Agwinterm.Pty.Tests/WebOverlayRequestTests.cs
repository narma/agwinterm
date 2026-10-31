using System.Text.Json;
using Xunit;

namespace Agwinterm.Pty.Tests;

public class WebOverlayRequestTests
{
    [Theory]
    [InlineData("{\"html\":\"a.html\",\"url\":\"https://example.com\"}")]
    [InlineData("{\"html\":\"a.html\",\"command\":\"cmd\"}")]
    [InlineData("{\"html\":\"a.html\",\"pane\":\"left\",\"width\":400}")]
    [InlineData("{\"html\":\"a.html\",\"pane\":\"invalid\"}")]
    [InlineData("{\"url\":\"https://example.com\",\"cwd\":\"C:/\"}")]
    [InlineData("{\"html\":\"a.html\",\"wait\":false}")]
    [InlineData("{}")]
    [InlineData("{\"html\":\"a.html\",\"persistent\":true}")]
    [InlineData("{\"url\":\"https://example.com\",\"block\":true}")]
    [InlineData("{\"html\":\"a.html\",\"companion\":true,\"pane\":\"left\"}")]
    [InlineData("{\"html\":\"a.html\",\"companion\":true,\"width\":400}")]
    [InlineData("{\"image\":\"a.png\",\"js\":false}")]
    [InlineData("{\"image\":\"a.png\",\"html\":\"a.html\"}")]
    [InlineData("{\"url\":\"https://example.com\",\"persistent\":\"true\"}")]
    [InlineData("{\"html\":\"a.html\",\"browse\":true}")]
    [InlineData("{\"url\":\"https://example.com\",\"browse\":\"true\"}")]
    [InlineData("{\"url\":\"https://example.com\",\"browse\":true,\"browse\":true}")]
    [InlineData("{\"url\":\"http://localhost/\",\"browse\":true,\"chromeless\":true}")]
    [InlineData("{\"url\":\"https://example.com\",\"chromeless\":true}")]
    [InlineData("{\"html\":\"a.html\",\"navigation\":true,\"chromeless\":true}")]
    [InlineData("{\"html\":\"a.html\",\"bridge\":\"review\"}")]
    [InlineData("{\"url\":\"https://example.com\",\"js\":true,\"bridge\":\"review\"}")]
    [InlineData("{\"html\":\"a.html\",\"js\":true,\"bridge\":\"all\"}")]
    [InlineData("{\"html\":\"a.html\",\"navigation\":1}")]
    [InlineData("{\"html\":\"a.html\",\"chromeless\":\"false\"}")]
    [InlineData("{\"html\":\"a.html\",\"width\":0}")]
    [InlineData("{\"html\":\"a.html\",\"height\":16385}")]
    [InlineData("{\"html\":\"a.html\",\"js\":true,\"js\":false}")]
    [InlineData("{\"html\":\"a.html\",\"js\":\"true\"}")]
    [InlineData("{\"html\":\"a.html\",\"width\":\"400\"}")]
    [InlineData("{\"html\":null}")]
    [InlineData("{\"html\":3}")]
    [InlineData("{\"html\":\"a.html\",\"html\":\"b.html\"}")]
    [InlineData("{\"url\":\"file:///C:/secret.txt\"}")]
    [InlineData("{\"url\":\"javascript:alert(1)\"}")]
    [InlineData("{\"url\":\"https://user:password@example.com\"}")]
    public void UnsafeOrAmbiguousRequestsRefuse(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => WebOverlayRequest.Parse(document.RootElement));
    }

    [Theory]
    [InlineData("{\"html\":\"report with spaces.html\"}")]
    [InlineData("{\"html\":\"report.html\",\"pane\":\"left\",\"cwd\":\"C:/reports\"}")]
    [InlineData("{\"url\":\"http://localhost:8080/dashboard\"}")]
    [InlineData("{\"url\":\"http://localhost:8080/dashboard\",\"persistent\":true}")]
    [InlineData("{\"html\":\"report.html\",\"companion\":true,\"block\":true}")]
    [InlineData("{\"image\":\"report.png\"}")]
    public void ScriptsAreOffUnlessExplicitlyGranted(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(WebOverlayRequest.Parse(document.RootElement).JavaScript);
        Assert.False(WebOverlayRequest.Parse(document.RootElement).Browse);
    }

    [Fact]
    public void BrowseIsExplicitAndDoesNotGrantScriptOrBridge()
    {
        using var doc = JsonDocument.Parse("{\"url\":\"https://google.com\",\"browse\":true}");
        var request = WebOverlayRequest.Parse(doc.RootElement);
        Assert.True(request.Browse);
        Assert.False(request.JavaScript);
        Assert.Null(request.Bridge);
    }
}
