using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class ControlObservationWaitTests
{
    private static (ControlObservation Journal, StatusSignal Left, StatusSignal Right) Setup()
    {
        var journal = new ControlObservation("W");
        var left = new StatusSignal(); var right = new StatusSignal();
        journal.ObservePane("L", left); journal.ObservePane("R", right);
        journal.SetMembership("S", ["L", "R"]);
        return (journal, left, right);
    }

    [Fact]
    public async Task TransientCompletionLatchesBeforeResetAndJournalWrap()
    {
        var (journal, left, _) = Setup();
        var wait = journal.WaitAsync("session", "S", new HashSet<AgentStatus> { AgentStatus.Completed });
        left.Set(AgentStatus.Completed, false, true);
        left.Set(AgentStatus.Idle, false, false);
        for (int i = 0; i < 1100; i++) journal.Publish("tree");
        var result = await wait;
        Assert.Equal("matched", result.Outcome);
        Assert.Equal("completed", result.Status);
        Assert.Equal("transition", result.Source);
        Assert.Equal("L", result.PaneId);
        Assert.Equal(2, result.Seq); // pane status then aggregate transition
        Assert.Equal(0, journal.PendingCount);
    }

    [Fact]
    public async Task ScopeAndStructuralRemovalFollowAggregatePriority()
    {
        var (journal, left, right) = Setup();
        left.Set(AgentStatus.Blocked, false, false);
        right.Set(AgentStatus.Completed, false, false);
        var statuses = new HashSet<AgentStatus> { AgentStatus.Completed };
        var sessionWait = journal.WaitAsync("session", "S", statuses);
        Assert.False(sessionWait.IsCompleted);
        Assert.Equal("snapshot", (await journal.WaitAsync("pane", "R", statuses)).Source);
        var removedWait = journal.WaitAsync("pane", "L", statuses);
        journal.RemovePane("L");
        Assert.Equal("completed", (await sessionWait).Status);
        Assert.Equal("target-gone", (await Assert.ThrowsAsync<ObservationException>(() => removedWait)).Code);
        left.Set(AgentStatus.Completed, false, false);
        Assert.Null(journal.Status("pane", "L"));
    }

    [Fact]
    public async Task AuxiliaryStatusDoesNotWinSessionAndHistoryDoesNotSatisfyNewWait()
    {
        var (journal, left, _) = Setup();
        var overlay = new StatusSignal();
        journal.ObservePane("O", overlay); journal.SetOwner("O", "S");
        overlay.Set(AgentStatus.Blocked, false, false);
        left.Set(AgentStatus.Completed, false, false); left.Set(AgentStatus.Idle, false, false);
        using var cancel = new CancellationTokenSource();
        var wait = journal.WaitAsync("session", "S", new HashSet<AgentStatus> { AgentStatus.Completed }, cancellationToken: cancel.Token);
        Assert.False(wait.IsCompleted);
        Assert.Equal(AgentStatus.Idle, journal.Status("session", "S")!.Value.Value.Status);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(0, journal.PendingCount);
    }

    [Theory]
    [InlineData(99, "matched")]
    [InlineData(100, "timeout")]
    [InlineData(101, "timeout")]
    public async Task DeadlineUsesCommitTimeNotDeliveryTime(long elapsed, string outcome)
    {
        var (journal, left, _) = Setup();
        long clock = 0; journal.UseClock(() => clock);
        var wait = journal.WaitAsync("pane", "L", new HashSet<AgentStatus> { AgentStatus.Completed }, 100);
        clock = elapsed;
        left.Set(AgentStatus.Completed, false, false);
        clock = 1000;
        var result = await wait;
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(elapsed, result.ElapsedMs);
        if (outcome == "timeout") { Assert.Null(result.Seq); Assert.Null(result.Source); Assert.Null(result.Status); }
    }

    [Fact]
    public async Task LongPollFiltersDoNotReturnExcludedFloodsAndTimeoutAdvancesCursor()
    {
        var journal = new ControlObservation("W");
        long clock = 0; journal.UseClock(() => clock);
        var pending = journal.ReadAsync(new(Kinds: new HashSet<string> { "overlay" }, WaitMs: 100));
        for (int i = 0; i < 1200; i++) journal.Publish("tree");
        Assert.False(pending.IsCompleted);
        clock = 100;
        journal.Publish("tree");
        var page = await pending;
        Assert.Empty(page.Events);
        Assert.Equal(1201, page.Cursor);
        Assert.False(page.Gap);
    }

    [Fact]
    public async Task CapacityIsSharedAndReleasedOnCancellationAndWindowClose()
    {
        var (journal, _, _) = Setup();
        using var cancel = new CancellationTokenSource();
        var waits = Enumerable.Range(0, 64).Select(_ => journal.WaitAsync("pane", "L", new HashSet<AgentStatus> { AgentStatus.Completed }, 0, cancel.Token)).ToArray();
        var polls = Enumerable.Range(0, 64).Select(_ => journal.ReadAsync(new(WaitMs: 25000), cancel.Token)).ToArray();
        Assert.Equal(128, journal.PendingCount);
        Assert.Equal("observer-limit", (await Assert.ThrowsAsync<ObservationException>(() => journal.ReadAsync(new(WaitMs: 25000)))).Code);
        cancel.Cancel();
        foreach (var task in waits.Cast<Task>().Concat(polls)) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, journal.PendingCount);
        var last = journal.WaitAsync("pane", "L", new HashSet<AgentStatus> { AgentStatus.Completed }, 0);
        journal.Close();
        Assert.Equal("window-gone", (await Assert.ThrowsAsync<ObservationException>(() => last)).Code);
    }

    [Fact]
    public void SameStatusFlagsPublishPaneEventWithoutFalseAggregateTransition()
    {
        var (journal, left, _) = Setup();
        left.Set(AgentStatus.Completed, false, false);
        long cursor = journal.LastSequence;
        left.Set(AgentStatus.Completed, true, true);
        var item = Assert.Single(journal.Read(new(cursor)).Events);
        Assert.Equal("status", item.GetProperty("type").GetString());
        Assert.Equal("L", item.GetProperty("session").GetString());
        Assert.Equal("L", item.GetProperty("paneId").GetString());
        Assert.Equal("S", item.GetProperty("sessionId").GetString());
        Assert.True(item.GetProperty("blink").GetBoolean());
        Assert.True(item.GetProperty("autoReset").GetBoolean());
    }
}
