using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class AgentRecoveryStateTests
{
    private static ShellCompletion Fresh() => new(Guid.NewGuid().ToString("N"), "");

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
