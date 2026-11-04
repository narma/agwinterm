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
        Assert.False(oldAttachment.FinishRestart(reservation, null));
        var newAttachment = oldAttachment; // ReplaceLivePane preserves the workload state
        Assert.Equal(pending, newAttachment.Sequence);
        oldAttachment.Command = "new session"; // the earlier accepted hook finishes later
        Assert.Equal("new session", newAttachment.Command);
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
