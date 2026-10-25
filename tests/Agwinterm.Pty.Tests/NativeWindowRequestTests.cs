using System.Text.Json;

namespace Agwinterm.Pty.Tests;

public class NativeWindowRequestTests
{
    [Theory]
    [InlineData("window.fullscreen", "{\"op\":false}")]
    [InlineData("window.fullscreen", "{\"op\":\"typo\"}")]
    [InlineData("window.fullscreen", "{\"op\":\"on\",\"op\":\"off\"}")]
    [InlineData("window.new", "{\"minimized\":\"true\"}")]
    [InlineData("window.move", "{\"monitor\":\"primary\",\"x\":1}")]
    [InlineData("window.move", "{\"monitor\":\"\"}")]
    [InlineData("session.reveal", "{\"path-only\":1}")]
    [InlineData("window.geometry", "[]")]
    public void MalformedRequestRefuses(string command, string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => NativeWindowRequest.Validate(command, doc.RootElement, null, null));
    }
    [Theory]
    [InlineData("window", "fullscreen", "typo")]
    [InlineData("window", "restore", "a", "--target", "b")]
    [InlineData("window", "move", "--monitor", "primary", "--x", "1")]
    [InlineData("window", "monitors", "a")]
    [InlineData("session", "reveal", "a")]
    public void CliRefusesBeforeConnecting(params string[] args) =>
        Assert.Equal(2, CtlRequestTests.RunCtl([..args, "--pipe", "missing-c7-" + Guid.NewGuid()]).ExitCode);
    [Fact] public void WindowAndSessionSelectorsHaveDistinctScopes()
    {
        var args = JsonSerializer.SerializeToElement(new { });
        Assert.Throws<ArgumentException>(() => NativeWindowRequest.Validate("window.restore", args, "a", "b"));
        NativeWindowRequest.Validate("session.reveal", args, "a", "b");
        Assert.Throws<ArgumentException>(() => NativeWindowRequest.Validate("window.monitors", args, "a", null));
    }
}
