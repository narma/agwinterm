using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class AgtermOverlayJobTests
{
    private static AgtermOriginPresentationScope Scope() => new("account", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
        new("Ubuntu", "alice", "origin", "/usr/bin/zmx", "/tmp/zmx"));
    private static AgtermOverlayJob Job(AgtermOriginPresentationScope scope, bool wait = false) =>
        new(scope, 10, 20, "pane", JsonSerializer.SerializeToElement(new { command = "exit 7", cwd = "/tmp" }), wait, 100);

    [Fact]
    public async Task OwnerRemovalCancelsOnceAndLateExitCannotReplaceOutcome()
    {
        var scope = Scope(); var job = Job(scope); int cancels = 0;
        job.Cancelled += () => cancels++;
        job.Claim(scope, 10, 20, 101);
        job.Close("owner-removed"); job.Close(); job.Finish("exit 0");
        Assert.Equal(1, cancels); Assert.Equal("owner-removed", await job.Completion); Assert.False(job.Occupied);
        var unclaimed = Job(scope); unclaimed.Close("owner-removed");
        Assert.Throws<IOException>(() => unclaimed.Claim(scope, 10, 20, 102));
    }

    [Fact]
    public void WrongScopeAndOwnerCannotConsumeSingleClaim()
    {
        var scope = Scope(); var job = Job(scope);
        foreach (var wrong in new[] { scope with { Account = "other" }, scope with { Session = Guid.NewGuid().ToString() },
            scope with { Window = Guid.NewGuid().ToString() }, scope with { Origin = scope.Origin with { User = "other" } } })
            Assert.Throws<IOException>(() => job.Claim(wrong, 10, 20, 101));
        Assert.Throws<IOException>(() => job.Claim(scope, 11, 20, 101));
        Assert.Throws<IOException>(() => job.Claim(scope, 10, 21, 101));
        Assert.False(job.Claimed);
        Assert.Equal("exit 7", job.Claim(scope, 10, 20, 101).GetProperty("command").GetString());
        Assert.Throws<IOException>(() => job.Claim(scope, 10, 20, 101));
    }

    [Fact]
    public void ExpiryAndPreclaimLossNeverLaunch()
    {
        var scope = Scope(); var expired = Job(scope); expired.Expire(15100);
        Assert.Equal("expired", expired.Result); Assert.False(expired.Occupied);
        Assert.Throws<IOException>(() => expired.Claim(scope, 10, 20, 15100));
        var lost = Job(scope); lost.PresentationLost();
        Assert.Throws<IOException>(() => lost.Claim(scope, 10, 20, 101));
        Assert.False(lost.Occupied);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaimedJobSurvivesPresentationLossAndSettlesOnlyOnce(bool wait)
    {
        var scope = Scope(); var job = Job(scope, wait); job.Claim(scope, 10, 20, 101);
        int cancels = 0; job.Cancelled += () => cancels++;
        job.PresentationLost(); job.Expire(30000);
        Assert.Null(job.Result); Assert.True(job.Occupied); Assert.Equal(0, cancels);
        job.Finish("exit 7"); job.Close(); job.Finish("exit 8");
        Assert.Equal("exit 7", await job.Completion); Assert.False(job.Occupied);
    }

    [Fact]
    public void HeldSurfaceReservesSlotUntilClosedOrPresentationLost()
    {
        var scope = Scope(); var job = Job(scope, true); job.Claim(scope, 10, 20, 101); job.Finish("exit 0");
        Assert.True(job.Occupied); job.PresentationLost(); Assert.False(job.Occupied);
        Assert.Equal("exit 0", job.Result);
    }

    [Fact]
    public void ConcurrentClaimsHaveExactlyOneWinner()
    {
        var scope = Scope(); var job = Job(scope); int wins = 0;
        Parallel.For(0, 64, _ => { try { job.Claim(scope, 10, 20, 101); Interlocked.Increment(ref wins); } catch (IOException) { } });
        Assert.Equal(1, wins);
    }
}
