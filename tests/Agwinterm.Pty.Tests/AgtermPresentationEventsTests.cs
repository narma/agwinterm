using System.Text.Json;
namespace Agwinterm.Pty.Tests;

public class AgtermPresentationEventsTests
{
    [Theory]
    [InlineData(null, "off", null)]
    [InlineData(null, "local.wav", "local.wav")]
    [InlineData("beep", "off", "beep")]
    [InlineData("Glass", "off", "default")]
    [InlineData("C:\\private.wav", "beep", "default")]
    [InlineData("\\\\server\\share\\sound.wav", "beep", "default")]
    public void ViewerUsesLocalPolicyAndNeverLoadsAnOriginPath(string? wire, string preference, string? expected)
    {
        Assert.Equal(expected, AgtermPresentationSound.ViewerSound(AgtermPresentationSound.Read(JsonSerializer.SerializeToElement(new { name = wire })), preference));
    }

    [Fact]
    public async Task EventsAreLiveSoundGoesOnlyToNegotiatedPresenterAndNeverReplays()
    {
        var broker = new AgtermPresentationBroker();
        var scope = new AgtermOriginPresentationScope("user", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), new("Ubuntu", "alice", "origin", "/usr/bin/zmx", "/tmp/zmx"));
        var snapshot = JsonSerializer.SerializeToElement(new { context = "state" });
        broker.Register(scope, snapshot, ["context", "notify", "sound"], presenterReady: true);
        using var first = broker.Subscribe(scope, new(1, ["context", "notify", "sound"], "presenter"));
        using var second = broker.Subscribe(scope, new(1, ["context", "notify", "sound"], "presenter"));
        Assert.True(first.AcquirePresenter()); Assert.False(second.AcquirePresenter());
        Assert.True(broker.SendSound(scope, null));
        Assert.False(broker.SendSound(scope, "beep", mirrored: true));
        var notification = JsonSerializer.SerializeToElement(new { title = "once", body = "body", source = "control" });
        broker.Publish(scope, snapshot, "notify", notification);
        broker.Publish(scope, snapshot, "notify", notification, mirrored: true);
        first.Dispose();
        Assert.True(second.AcquirePresenter()); Assert.True(broker.SendSound(scope, "beep"));
        second.Dispose();
        Assert.False(broker.SendSound(scope, "beep"));
        using var reconnect = broker.Subscribe(scope, new(1, ["context", "notify"], "presenter"));
        Assert.True(reconnect.AcquirePresenter()); Assert.False(broker.SendSound(scope, null));
        reconnect.Dispose();
        async Task<List<AgtermPresentationFrame>> Drain(AgtermPresentationBroker.Subscription sub)
        { var list = new List<AgtermPresentationFrame>(); await foreach (var frame in sub.ReadAllAsync(default)) list.Add(frame); return list; }
        var a = await Drain(first); var b = await Drain(second); var c = await Drain(reconnect);
        Assert.Single(a, f => f.Kind == "sound"); Assert.Single(b, f => f.Kind == "sound");
        Assert.Single(a, f => f.Kind == "notify"); Assert.Single(b, f => f.Kind == "notify");
        Assert.DoesNotContain(c, f => f.Kind is "sound" or "notify");
        Assert.False(c.Single(f => f.Kind == "snapshot").Body.TryGetProperty("sound", out _));
    }

    [Theory]
    [InlineData("notify")]
    [InlineData("sound")]
    public void DuplicateAndOldGenerationEventsHaveNoEffect(string kind)
    {
        var state = new AgtermPresentationConnection(); var now = DateTimeOffset.UtcNow;
        long old = state.Begin(1, [kind], now);
        state.Accept(old, new("hello", 1, 0, JsonSerializer.SerializeToElement(new { version = 1, mode = "presenter", kinds = new[] { kind } })), now);
        state.Accept(old, new("snapshot", 1, 1, JsonSerializer.SerializeToElement(new { })), now);
        var frame = new AgtermPresentationFrame(kind, 1, 2, JsonSerializer.SerializeToElement(new { }));
        Assert.Equal(AgtermPresentationDisposition.Delta, state.Accept(old, frame, now));
        Assert.Equal(AgtermPresentationDisposition.Ignored, state.Accept(old, frame, now));
        state.Begin(1, [kind], now);
        Assert.Equal(AgtermPresentationDisposition.Ignored, state.Accept(old, frame with { Revision = 999 }, now));
    }
}
