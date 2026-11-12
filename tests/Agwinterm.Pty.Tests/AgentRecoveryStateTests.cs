using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class AgentRecoveryStateTests
{
    private static ShellCompletion Fresh() => new(Guid.NewGuid().ToString("N"), "");

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AttentionRoundTripPreservesReadStateAndLatestProducerStatus(bool seen, bool idleAfterDone)
    {
        var activity = new AgentActivity();
        activity.Observe(AgentStatus.Active, false);
        activity.Observe(AgentStatus.Completed, false);
        if (idleAfterDone) activity.Observe(AgentStatus.Idle, false);
        activity.Display(seen);
        Assert.True(activity.Restore(activity.Capture()! with { ChangedAt = 123456 }));
        var pane = new PaneState { AgentAttention = activity.Capture() };
        string json = System.Text.Json.JsonSerializer.Serialize(pane, RestoreState.Json);
        var saved = System.Text.Json.JsonSerializer.Deserialize<PaneState>(json)!;
        var restored = new AgentActivity();
        Assert.True(restored.Restore(saved.AgentAttention));
        // The restored session's StatusChanged callback must not change read state either.
        restored.Observe(saved.AgentAttention!.RawStatus, false);
        Assert.Equal(seen ? AgentStatus.Idle : AgentStatus.Completed, restored.Display(false));
        Assert.Equal(activity.ChangedAt, restored.ChangedAt);
        Assert.Equal(activity.Capture(), restored.Capture());
        restored.Observe(AgentStatus.Active, false);
        Assert.Equal(AgentStatus.Active, restored.Display(false));
    }

    [Fact]
    public void AbsentOrInvalidAttentionCannotCreateAnAgent()
    {
        var activity = new AgentActivity();
        Assert.Null(activity.Capture());
        Assert.False(activity.Restore(null));
        Assert.False(activity.Restore(new((AgentStatus)99, AgentStatus.Idle, false, 1)));
        Assert.False(activity.Restore(new(AgentStatus.Idle, (AgentStatus)99, false, 1)));
        Assert.False(activity.Restore(new(AgentStatus.Idle, AgentStatus.Idle, false, -1)));
        Assert.False(activity.Reported);
        activity.Observe(AgentStatus.Completed, false);
        activity.Reset();
        Assert.Null(activity.Capture());
    }

    [Fact]
    public void OnlyAnAdoptedMatchingUnfinishedBindingRestoresPresence()
    {
        var completion = Fresh();
        var state = new AgentRecoveryState { Command = "claude --resume live", Completion = completion,
            Token = completion.Token, Generation = 1 };
        Assert.True(completion.Accept(completion.Token, 1, false));
        Assert.True(state.HasSurvivingBinding(true));
        Assert.False(state.HasSurvivingBinding(false));
        state.Token = Fresh().Token;
        Assert.False(state.HasSurvivingBinding(true));
        state.Token = null;
        Assert.False(state.HasSurvivingBinding(true));
        state.Token = completion.Token;
        state.Command = null;
        Assert.False(state.HasSurvivingBinding(true));
        state.Command = "claude --resume exited";
        Assert.True(completion.Accept(completion.Token, 2, false));
        Assert.False(state.HasSurvivingBinding(true));
    }

    [Fact]
    public void SavedHookStatusRoundTripsWithoutChangingLegacyPaneShape()
    {
        var pane = new PaneState();
        Assert.DoesNotContain("AgentStatus", System.Text.Json.JsonSerializer.Serialize(pane, RestoreState.Json));
        pane.AgentStatus = AgentStatus.Blocked;
        string json = System.Text.Json.JsonSerializer.Serialize(pane, RestoreState.Json);
        Assert.Equal(AgentStatus.Blocked, System.Text.Json.JsonSerializer.Deserialize<PaneState>(json)!.AgentStatus);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("claude --resume replacement")]
    public void ReplacementBindingOrClearWinsOverRestartFallback(string? replacement)
    {
        var state = new AgentRecoveryState { Command = "old" };
        int reservation = state.BeginRestart(Fresh());
        Interlocked.Increment(ref state.Sequence); // replacement's control request
        state.Command = replacement; // its queued binding lands before the restart reply
        state.Token = state.Completion.Token;
        Assert.False(state.FinishRestart(reservation, "old"));
        Assert.Equal(replacement, state.Command);
    }

    [Fact]
    public void PendingBindingWinsAndCanLandThroughTheOldAttachment()
    {
        var oldAttachment = new AgentRecoveryState { Command = "old" };
        int oldRequest = oldAttachment.Sequence;
        int reservation = oldAttachment.BeginRestart(Fresh());
        Assert.NotEqual(oldRequest, oldAttachment.Sequence);
        int pending = Interlocked.Increment(ref oldAttachment.Sequence);
        Assert.True(oldAttachment.FinishRestart(reservation, null));
        Assert.Null(oldAttachment.Command); // safe fallback until the hook actually succeeds
        var newAttachment = oldAttachment; // ReplaceLivePane preserves the workload state
        Assert.Equal(pending, newAttachment.Sequence);
        oldAttachment.Command = "new session"; // the earlier accepted hook finishes later
        Assert.Equal("new session", newAttachment.Command);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("selected agent")]
    public void IgnoredReplacementRequestDoesNotRetainOldConversation(string? fallback)
    {
        var state = new AgentRecoveryState { Command = "old conversation", Token = Fresh().Token };
        int reservation = state.BeginRestart(Fresh());
        Interlocked.Increment(ref state.Sequence); // ancestry lookup fails; no binding is written
        Assert.True(state.FinishRestart(reservation, fallback));
        Assert.Equal(fallback, state.Command);
        Assert.Equal(state.Completion.Token, state.Token);
    }

    [Fact]
    public void LateRestartResultCannotOverwriteANewerReservationOrCompletedWork()
    {
        var state = new AgentRecoveryState { Command = "old" };
        int first = state.BeginRestart(Fresh());
        int second = state.BeginRestart(Fresh());
        Assert.False(state.FinishRestart(first, "wrong"));
        Assert.True(state.Completion.Accept(state.Completion.Token, 1, false));
        Assert.True(state.FinishRestart(second, "already finished"));
        Assert.Null(state.Command);
    }

    [Fact]
    public void ReservationKeepsRecoveryUntilSuccessfulReplacement()
    {
        var state = new AgentRecoveryState { Command = "old" };
        var completion = Fresh();
        int reservation = state.BeginRestart(completion);
        Assert.Equal("old", state.Command); // a failed reservation retains recovery
        Assert.True(state.FinishRestart(reservation, "resumed"));
        Assert.Equal("resumed", state.Command);
        Assert.Equal(completion.Token, state.Token);
        Assert.Equal(0, state.Generation);
    }
}
