using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class AgtermPresentationBrokerTests
{
    private static AgtermOriginPresentationScope Scope() => new("account", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
        new("Ubuntu", "alice", "origin", "/usr/bin/zmx", "/tmp/zmx"));
    private static JsonElement Snapshot(string value) => JsonSerializer.SerializeToElement(new { context = value });
    private static AgtermPresentationHello Hello() => new(1, ["context"], "mirror");

    [Fact]
    public async Task SnapshotAlwaysPrecedesDeltasAndMirroredPublicationIsIgnored()
    {
        var broker = new AgtermPresentationBroker();
        var scope = Scope();
        broker.Register(scope, Snapshot("initial"), ["context"]);
        using var subscription = broker.Subscribe(scope, Hello());
        broker.Publish(scope, Snapshot("next"), "context", JsonSerializer.SerializeToElement("next"));
        broker.Publish(scope, Snapshot("mirrored"), "context", JsonSerializer.SerializeToElement("mirrored"), mirrored: true);
        broker.Retire(scope);
        var frames = new List<AgtermPresentationFrame>();
        await foreach (var frame in subscription.ReadAllAsync(default)) frames.Add(frame);
        Assert.Equal(new[] { "hello", "snapshot", "context" }, frames.Select(f => f.Kind));
        Assert.Equal("initial", frames[1].Body.GetProperty("context").GetString());
        Assert.Equal(new long[] { 0, 1, 2 }, frames.Select(f => f.Revision));
    }

    [Fact]
    public void ScopeDoesNotPermitWrongAccountWindowSessionOrOrigin()
    {
        var broker = new AgtermPresentationBroker();
        var scope = Scope();
        broker.Register(scope, Snapshot("initial"), ["context"]);
        foreach (var other in new[] { scope with { Account = "other" }, scope with { Window = Guid.NewGuid().ToString() },
            scope with { Session = Guid.NewGuid().ToString() }, scope with { Origin = scope.Origin with { User = "other" } } })
            Assert.Throws<IOException>(() => broker.Subscribe(other, Hello()));
        Assert.Throws<IOException>(() => broker.Publish(scope, Snapshot("bad"), "session.type", JsonSerializer.SerializeToElement(new { text = "execute" })));
    }

    [Fact]
    public async Task QueueOverflowDisconnectsSubscriberAndRetireRevokesEntry()
    {
        var broker = new AgtermPresentationBroker();
        var scope = Scope();
        broker.Register(scope, Snapshot("initial"), ["context"]);
        using var subscription = broker.Subscribe(scope, Hello());
        for (int i = 0; i < 300; i++) broker.Publish(scope, Snapshot("next"), "context", JsonSerializer.SerializeToElement("next"));
        int count = 0;
        await foreach (var frame in subscription.ReadAllAsync(default)) count++;
        Assert.Equal(256, count);
        broker.Retire(scope);
        Assert.Throws<IOException>(() => broker.Subscribe(scope, Hello()));
    }
}
