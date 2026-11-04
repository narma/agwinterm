using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class RestoreCompletionTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private const string OtherToken = "fedcba9876543210fedcba9876543210";

    [Fact]
    public void DetachedCompletionInvalidatesBothAutomaticRecoveryPathsAfterRoundTrip()
    {
        var pane = new PaneState { Command = "claude --resume abc", CommandToken = Token, CommandGeneration = 2,
            AgentResume = "claude --resume abc", AgentResumeToken = Token, AgentResumeGeneration = 2,
            RestoreCommand = "explicit user pin" };
        var state = new AppState { Workspaces = [new WorkspaceState { Sessions = [new SessionState { Panes = [pane] }] }] };
        Assert.True(RestoreState.TryDeserialize(RestoreState.Serialize(state), out var loaded));
        var restored = loaded!.Workspaces[0].Sessions[0].Panes[0];
        RestoreState.ReconcileCompletion(restored, token => token == Token ? 3 : 0);
        Assert.Null(restored.AgentResume);
        Assert.Equal("", restored.Command);
        Assert.Equal("explicit user pin", restored.RestoreCommand);
    }

    [Theory]
    [InlineData(0)] // no checkpoint available: keep recovery
    [InlineData(2)] // captured command is still running
    public void NoLaterPromptPreservesRecovery(long checkpoint)
    {
        var pane = new PaneState { Command = "claude", CommandToken = Token, CommandGeneration = 2 };
        RestoreState.ReconcileCompletion(pane, _ => checkpoint);
        Assert.Equal("claude", pane.Command);
    }

    [Fact]
    public void LaterCaptureAndAnotherShellAreIndependentOfAnOldAgentBinding()
    {
        foreach (var token in new[] { Token, OtherToken })
        {
            var pane = new PaneState { AgentResume = "claude", AgentResumeToken = Token, AgentResumeGeneration = 1,
                Command = "npm test", CommandToken = token, CommandGeneration = 2 };
            RestoreState.ReconcileCompletion(pane, t => t == Token ? 2 : 0);
            Assert.Null(pane.AgentResume);
            Assert.Equal("npm test", pane.Command);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("../invalid")]
    public void LegacyOrInvalidCaptureMetadataDoesNotReadACheckpoint(string? token)
    {
        var pane = new PaneState { Command = "legacy", CommandToken = token };
        RestoreState.ReconcileCompletion(pane, _ => throw new Exception("must not read"));
        Assert.Equal("legacy", pane.Command);
    }
}
