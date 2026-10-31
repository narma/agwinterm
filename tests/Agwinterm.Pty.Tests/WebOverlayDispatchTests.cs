using System.Text.Json;

namespace Agwinterm.Pty.Tests;

public class WebOverlayDispatchTests
{
    [Theory]
    [InlineData("{\"cmd\":\"session.overlay\",\"target\":\"a\",\"target\":\"b\",\"args\":{\"html\":\"x.html\"}}")]
    [InlineData("{\"cmd\":\"session.overlay\",\"target\":\"\",\"args\":{\"html\":\"x.html\"}}")]
    [InlineData("{\"cmd\":\"session.overlay\",\"window\":\"missing\",\"args\":{\"html\":\"x.html\"}}")]
    [InlineData("{\"cmd\":\"session.overlay\",\"target\":42,\"args\":{\"html\":\"x.html\"}}")]
    [InlineData("{\"cmd\":\"session.overlay\",\"extra\":true,\"args\":{\"html\":\"x.html\"}}")]
    [InlineData("{\"cmd\":\"session.overlay\",\"args\":{\"action\":\"open\",\"html\":\"x.html\",\"js\":1}}")]
    [InlineData("{\"cmd\":\"session.overlay\",\"args\":{\"action\":\"reload\",\"pane\":\"invalid\"}}")]
    [InlineData("{\"cmd\":\"session.overlay\",\"args\":{\"action\":\"result\",\"page\":42}}")]
    public void InvalidRawRequestsNeverReachTheHost(string json)
    {
        var host = new FakeSessionHost();
        using var reply = JsonDocument.Parse(new ControlServer(host).Dispatch(json));
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Null(host.LastWebRequest); Assert.Null(host.LastWebCommand);
    }

    [Fact]
    public void TypedOpenAndReloadKeepOwnerAndPageReply()
    {
        var host = new FakeSessionHost(); var server = new ControlServer(host);
        using var reply = JsonDocument.Parse(server.Dispatch("""{"cmd":"session.overlay","target":"session-id","args":{"action":"open","html":"report.html","js":true,"bridge":"review","width":700}}"""));
        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("test-web-page", reply.RootElement.GetProperty("result").GetProperty("pageID").GetString());
        Assert.Equal("session-id", host.LastWebTarget);
        Assert.Equal("review", host.LastWebRequest!.Bridge); Assert.Equal(700, host.LastWebRequest.Width);
        using var reload = JsonDocument.Parse(server.Dispatch("""{"cmd":"session.overlay","target":"session-id","args":{"action":"reload"}}"""));
        Assert.True(reload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("reload", host.LastWebCommand!.Action);
        Assert.False(host.LastWebCommand.Current);
        using var current = JsonDocument.Parse(server.Dispatch("""{"cmd":"session.overlay","target":"session-id","args":{"action":"reload","current":true}}"""));
        Assert.True(current.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(host.LastWebCommand.Current);
    }

    [Theory]
    [InlineData("reload", "{\"current\":\"true\"}")]
    [InlineData("reload", "{\"current\":true,\"current\":false}")]
    [InlineData("navigate", "{\"direction\":\"back\",\"current\":true}")]
    public void CurrentReloadHasStrictOperationSpecificType(string action, string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => WebOverlayCommand.Parse(action, doc.RootElement));
    }
}
