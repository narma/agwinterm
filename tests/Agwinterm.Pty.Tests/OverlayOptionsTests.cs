using System.Text.Json;
using Xunit;

namespace Agwinterm.Pty.Tests;

public class OverlayOptionsTests
{
    [Theory]
    [InlineData("open", "{\"width\":0}")]
    [InlineData("open", "{\"width\":1.5}")]
    [InlineData("open", "{\"height\":16385}")]
    [InlineData("open", "{\"background-color\":\"#abcdef00\"}")]
    [InlineData("open", "{\"background-color\":\"#abcdez\"}")]
    [InlineData("open", "{\"cwd\":\" \"}")]
    [InlineData("open", "{\"follow\":\"true\"}")]
    [InlineData("hide", "{\"cwd\":\"x\"}")]
    [InlineData("resize", "{\"full\":true,\"width\":100}")]
    [InlineData("open", "{\"pane\":\"left\",\"height\":100}")]
    [InlineData("open", "{\"width\":100,\"width\":200}")]
    [InlineData("typo", "{}")]
    [InlineData("open", "{\"action\":3,\"command\":\"cmd\"}")]
    [InlineData("close", "{\"size-percent\":50}")]
    public void InvalidOptionsRefuseBeforeHost(string action, string json)
        => Assert.Throws<ArgumentException>(() => OverlayOptions.Parse(action, JsonDocument.Parse(json).RootElement));

    [Fact]
    public void AbsenceAndMixedDimensionsRemainDistinct()
    {
        var options = OverlayOptions.Parse("open", JsonDocument.Parse("{\"size-percent\":60,\"width\":400,\"follow\":true,\"background-color\":\"#12aB34\"}").RootElement);
        Assert.Equal(60, options.Percent); Assert.Equal(400, options.Width); Assert.Null(options.Height);
        Assert.True(options.Follow); Assert.True(options.Extended);
        Assert.False(OverlayOptions.Parse("resize", JsonDocument.Parse("{}").RootElement).HasSize);
    }

    [Theory]
    [InlineData("width", "0")]
    [InlineData("height", "1.5")]
    [InlineData("background-color", "#wrong")]
    public void InvalidCliNeverConnects(string option, string value)
    {
        var result = CtlRequestTests.RunCtl("session", "overlay", "open", "cmd", "--" + option, value,
            "--pipe", "absent-c2-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Nothing sent", result.StdErr);
    }

    [Fact]
    public void RawInvalidReplacementPreservesExistingSlot()
    {
        var host = new FakeSessionHost();
        using var server = new ControlServer(host);
        server.Dispatch("{\"cmd\":\"session.overlay\",\"args\":{\"command\":\"cmd\"}}");
        Assert.True(host.ActiveSess!.Overlay);
        using var response = JsonDocument.Parse(server.Dispatch("{\"cmd\":\"session.overlay\",\"args\":{\"command\":\"exit\",\"width\":0}}"));
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(host.ActiveSess.Overlay);
    }
}
