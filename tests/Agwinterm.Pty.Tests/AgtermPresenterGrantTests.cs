using System.Text.Json;

namespace Agwinterm.Pty.Tests;

public class AgtermPresenterGrantTests
{
    [Fact] public void PageClaimCannotMoveToAnotherPresenterAfterDisconnect()
    {
        var broker = new AgtermPresentationBroker();
        var scope = new AgtermOriginPresentationScope("account", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), new("Ubuntu", "alice", "origin", "/usr/bin/zmx", "/tmp/zmx"));
        broker.Register(scope, JsonSerializer.SerializeToElement(new { }), [], presenterReady: true);
        var hello = new AgtermPresentationHello(1, [], "presenter");
        using var first = broker.Subscribe(scope, hello);
        using var second = broker.Subscribe(scope, hello);
        Assert.True(first.AcquirePresenter());
        var grant = new AgtermPageGrant(scope, first.Generation, broker.PresenterOwner(scope, first)!.Value, true, 0);
        Assert.Throws<IOException>(() => broker.ClaimPage(grant, second, 1));
        Assert.False(grant.IsClaimed);
        first.Dispose(); Assert.True(second.AcquirePresenter());
        Assert.Throws<IOException>(() => broker.ClaimPage(grant, first, 2));
        Assert.Throws<IOException>(() => broker.ClaimPage(grant, second, 2));
        Assert.False(grant.IsClaimed);
    }
    [Fact] public void FirstAcquireWinsUntilDisconnectAndOldAnswersLoseAuthority()
    {
        var grant = new AgtermPresenterGrant();
        Assert.True(grant.Acquire(101)); long first = grant.Generation;
        Assert.True(grant.Acquire(101)); Assert.Equal(first, grant.Generation);
        Assert.False(grant.Acquire(102)); Assert.False(grant.Release(102));
        Assert.True(grant.Authorizes(101, first));
        Assert.True(grant.Release(101)); Assert.False(grant.Authorizes(101, first));
        Assert.True(grant.Acquire(102)); Assert.True(grant.Generation > first);
        Assert.False(grant.Authorizes(102, first)); Assert.True(grant.Authorizes(102, grant.Generation));
    }

    [Fact] public void BrokerRetirementAndUnreadyOriginsCannotGrantPresenterAuthority()
    {
        var broker = new AgtermPresentationBroker();
        var scope = new AgtermOriginPresentationScope("account", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), new("Ubuntu", "alice", "origin", "/usr/bin/zmx", "/tmp/zmx"));
        var hello = new AgtermPresentationHello(1, [], "presenter");
        broker.Register(scope, JsonSerializer.SerializeToElement(new {}), []);
        using (var unready = broker.Subscribe(scope, hello)) { Assert.Equal("mirror", unready.Mode); Assert.False(unready.AcquirePresenter()); }
        broker.Retire(scope);
        broker.Register(scope, JsonSerializer.SerializeToElement(new {}), [], presenterReady: true);
        using var first = broker.Subscribe(scope, hello);
        using var second = broker.Subscribe(scope, hello);
        Assert.True(first.AcquirePresenter()); var owner = broker.PresenterOwner(scope, first)!.Value;
        Assert.False(second.AcquirePresenter()); Assert.True(broker.Authorizes(scope, first, owner));
        first.Dispose(); Assert.False(broker.Authorizes(scope, first, owner));
        Assert.True(second.AcquirePresenter()); Assert.False(broker.Authorizes(scope, second, owner));
        broker.Retire(scope); Assert.Null(broker.PresenterOwner(scope, second));
    }
}
