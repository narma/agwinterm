using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class AgtermPresentationConnectionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static AgtermPresentationFrame Hello(long gen = 1) => new("hello", gen, 0,
        JsonSerializer.SerializeToElement(new { version = 1, kinds = new[] { "status", "notify" }, mode = "mirror" }));
    private static AgtermPresentationFrame Snapshot(long gen = 1) => new("snapshot", gen, 1, JsonSerializer.SerializeToElement(new { }));

    [Fact]
    public void OldTransportCannotMutateReconnectedOrClosedState()
    {
        var state = new AgtermPresentationConnection();
        long old = state.Begin(1, ["status"], Now);
        state.Accept(old, Hello(), Now);
        state.Accept(old, Snapshot(), Now);
        long current = state.Begin(1, ["status"], Now);
        state.Accept(current, Hello(2), Now);
        state.Accept(current, Snapshot(2), Now);
        Assert.Equal(AgtermPresentationDisposition.Ignored, state.Accept(old, new("status", 1, 999, default), Now));
        Assert.Equal(2, state.Generation);
        Assert.Null(state.Fail(old, "old failure"));
        Assert.Equal(AgtermPresentationPhase.Connected, state.Phase);
        state.Close();
        Assert.Equal(AgtermPresentationDisposition.Ignored, state.Accept(current, new("status", 2, 999, default), Now));
    }

    [Fact]
    public void SnapshotsAndUnknownKindsPreserveOrderingWithoutEvents()
    {
        var state = new AgtermPresentationConnection();
        long launch = state.Begin(1, ["status"], Now);
        state.Accept(launch, Hello(), Now);
        Assert.Equal(AgtermPresentationDisposition.Snapshot, state.Accept(launch, Snapshot(), Now));
        Assert.Equal(AgtermPresentationDisposition.Ignored, state.Accept(launch, new("ask.future", 1, 5, default), Now));
        Assert.Equal(5, state.Revision);
        Assert.Equal(AgtermPresentationDisposition.Ignored, state.Accept(launch, new("status", 1, 4, default), Now));
        Assert.Equal(AgtermPresentationDisposition.Ignored, state.Accept(launch, new("notify", 1, 6, default), Now));
        Assert.Equal(AgtermPresentationDisposition.Delta, state.Accept(launch, new("status", 1, 7, default), Now));
    }

    [Fact]
    public void MissingHelloSnapshotAndSilenceAreBounded()
    {
        var state = new AgtermPresentationConnection();
        long launch = state.Begin(1, [], Now);
        Assert.Throws<IOException>(() => state.Accept(launch, Snapshot(), Now));
        Assert.True(state.Expired(launch, Now.AddSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)));
        state.Accept(launch, Hello(), Now);
        Assert.Throws<IOException>(() => state.Accept(launch, new("status", 1, 1, default), Now));
        Assert.True(state.Expired(launch, Now.AddSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)));
        state.Accept(launch, Snapshot(), Now);
        Assert.False(state.Expired(launch, Now.AddSeconds(29), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)));
        Assert.True(state.Expired(launch, Now.AddSeconds(30), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void RetryBudgetIsBoundedAndUnsupportedNeverLaunches()
    {
        var state = new AgtermPresentationConnection();
        foreach (int delay in new[] { 1, 2, 4, 8, 16 })
        {
            long launch = state.Begin(1, [], Now);
            Assert.Equal(TimeSpan.FromSeconds(delay), state.Fail(launch, "lost"));
        }
        Assert.Null(state.Fail(state.Begin(1, [], Now), "lost"));
        state.Begin(null, [], Now);
        Assert.Equal(AgtermPresentationPhase.Unsupported, state.Phase);
        Assert.Null(state.Fail(state.Launch, "unavailable"));
    }
}
