using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class WslLiveControlTests
{
    private sealed class WindowRouter : IWindowHost
    {
        public readonly FakeSessionHost First = new(), Second = new();
        public ISessionHost? ResolveWindow(string? selector) => selector switch
        {
            null or "active" or "window-first" => First,
            "window-second" or "window-s" => Second,
            _ => null
        };
        public IReadOnlyList<WindowSnapshot> Windows() => [new("window-first", "first", true, true), new("window-second", "second", true, false)];
        public string WindowNew(string? n) => throw new NotSupportedException();
        public bool WindowSelect(string? s) => throw new NotSupportedException();
        public bool WindowClose(string? s) => throw new NotSupportedException();
        public bool WindowDelete(string? s) => throw new NotSupportedException();
        public bool WindowRename(string? s, string n) => throw new NotSupportedException();
        public bool WindowResize(string? s, int w, int h) => throw new NotSupportedException();
        public bool WindowMove(string? s, int x, int y) => throw new NotSupportedException();
        public bool WindowZoom(string? s) => throw new NotSupportedException();
    }

    [Fact]
    public void ExactWindowDispatchDoesNotUseActiveOrPrefixFallback()
    {
        var windows = new WindowRouter();
        using var server = new ControlServer(windows.First, windows, "unused");
        foreach (string selector in new[] { "window-s", "active", "missing" })
            Assert.Contains("\"ok\":false", server.Dispatch(JsonSerializer.Serialize(new { cmd = "zmx.retry", target = "pane", window = selector })));
        Assert.Equal(0, windows.First.LiveCalls);
        Assert.Equal(0, windows.Second.LiveCalls);
        Assert.Contains("\"ok\":true", server.Dispatch("{\"cmd\":\"zmx.retry\",\"target\":\"pane\",\"window\":\"window-second\"}"));
        Assert.Equal(0, windows.First.LiveCalls);
        Assert.Equal(1, windows.Second.LiveCalls);
    }
    [Theory]
    [InlineData("{\"cmd\":\"zmx.retry\",\"target\":\"pane\",\"target\":\"other\"}")]
    [InlineData("{\"cmd\":\"zmx.retry\",\"target\":\"pane\",\"unknown\":true}")]
    [InlineData("{\"cmd\":\"zmx.retry\",\"target\":\"active\"}")]
    [InlineData("{\"cmd\":\"zmx.retry\",\"target\":\"pane\",\"window\":\"missing\"}")]
    [InlineData("{\"cmd\":\"zmx.terminate\",\"target\":\"pane\",\"args\":{\"force\":false}}")]
    public void InvalidRawEnvelopeNeverDispatches(string request)
    {
        var host = new FakeSessionHost();
        using var server = new ControlServer(host);
        Assert.Contains("\"ok\":false", server.Dispatch(request));
        Assert.Equal(0, host.LiveCalls);
    }

    [Fact]
    public void UncertainMutationIsNotRetriedByDispatch()
    {
        var host = new FakeSessionHost { LiveHandler = (_, _, _) => throw new WslRecoveryException("uncertain", "lost acknowledgement") };
        using var server = new ControlServer(host);
        Assert.Contains("\"ok\":false", server.Dispatch("{\"cmd\":\"zmx.terminate\",\"target\":\"pane\",\"args\":{\"force\":true}}"));
        Assert.Equal(1, host.LiveCalls);
    }

    [Fact]
    public void AuthoritativeInputStillValidatesControlBytesBeforeDispatch()
    {
        int deliveries = 0;
        var host = new FakeSessionHost { LiveTerminalHandler = (_, _, _) =>
        {
            deliveries++;
            return JsonSerializer.SerializeToElement(new { accepted = true, executionComplete = false });
        } };
        using var server = new ControlServer(host);
        server.Dispatch("{\"cmd\":\"session.new\"}");
        Assert.Contains("\"ok\":false", server.Dispatch("{\"cmd\":\"session.type\",\"args\":{\"text\":\"unsafe\\u0000suffix\"}}"));
        Assert.Equal(0, deliveries);
        Assert.Contains("\"accepted\":true", server.Dispatch("{\"cmd\":\"session.type\",\"args\":{\"text\":\"safe\"}}"));
        Assert.Equal(1, deliveries);
    }

    [Theory]
    [InlineData("zmx.retry", "{}", null)]
    [InlineData("zmx.retry", "{}", "active")]
    [InlineData("zmx.retry", "{\"unknown\":1}", "pane")]
    [InlineData("zmx.restart", "{\"source\":\"shell\",\"source\":\"agent\"}", "pane")]
    [InlineData("zmx.restart", "{\"source\":\"all\"}", "pane")]
    [InlineData("zmx.terminate", "{\"force\":false}", "pane")]
    [InlineData("zmx.list", "{\"profile\":\"test\"}", "pane")]
    public void AmbiguousOrInvalidControlCannotReachMutation(string command, string json, string? target)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => WslLiveControl.Validate(command, document.RootElement, target));
    }

    [Theory]
    [InlineData("zmx.retry", "{}")]
    [InlineData("zmx.detach", "{}")]
    [InlineData("zmx.terminate", "{\"force\":true}")]
    [InlineData("zmx.restart", "{\"source\":\"pin\"}")]
    public void ExplicitPaneControlValidates(string command, string json)
    {
        using var document = JsonDocument.Parse(json);
        WslLiveControl.Validate(command, document.RootElement, "exact-pane-id");
    }
}
