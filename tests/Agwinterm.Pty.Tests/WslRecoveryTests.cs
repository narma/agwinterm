using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class WslRecoveryTests
{
    [Fact]
    public void RoleProtocolIsConsumedWithoutChangingTheApplicationTitle()
    {
        var role = new ZmxRoleState("current");
        using var terminal = new Agwinterm.Core.GhosttyTerminalCore(80, 24);
        terminal.SetTitleFilter(text =>
        {
            if (!text.StartsWith("zmx-role;", StringComparison.Ordinal)) return false;
            if (ZmxRoleNotice.Parse(text) is { } notice) role.Apply(notice);
            return true;
        });
        terminal.Feed(System.Text.Encoding.UTF8.GetBytes("\x1b]2;editor\x07"));
        terminal.Feed(System.Text.Encoding.UTF8.GetBytes("\x1b]2;zmx-role;current:leader:8\x07"));
        terminal.Feed(System.Text.Encoding.UTF8.GetBytes("\x1b]2;zmx-role;old:follower:99\x07"));
        terminal.Feed(System.Text.Encoding.UTF8.GetBytes("\x1b]2;zmx-role;current:follower:7\x07"));
        Assert.Equal("editor", terminal.Title);
        Assert.Equal("leader", role.Role);
        Assert.Equal(8u, role.Generation);
    }
    [Fact]
    public async Task CloseInvalidatesAResponseAlreadyQueuedForPublication()
    {
        var gate = new WslRecoveryGate();
        Assert.True(gate.TryBegin(out long token));
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int transports = 0;
        var callback = Task.Run(async () => { await reply.Task; return gate.Publish(token, () => transports++); });
        Assert.False(gate.TryBegin(out _));
        gate.Cancel();
        reply.SetResult();
        Assert.False(await callback);
        gate.Complete(token);
        Assert.Equal(0, transports);
        Assert.False(gate.TryBegin(out _));
        gate.Reopen();
        Assert.True(gate.TryBegin(out long retry));
        gate.Complete(token); // a late finally cannot clear the retry's active operation
        Assert.False(gate.TryBegin(out _));
        Assert.False(gate.Publish(token, () => transports++));
        Assert.True(gate.Publish(retry, () => transports++));
        Assert.Equal(1, transports);
    }

    [Theory]
    [InlineData("missing", "missing")]
    [InlineData("unavailable", "unavailable")]
    [InlineData("unsupported", "unsupported")]
    [InlineData("timeout", "uncertain")]
    [InlineData("ready", "uncertain")]
    public void FailedRequestsNeverManufactureSuccessfulRecovery(string reported, string expected)
        => Assert.Equal(expected, new WslRecoveryException(reported, "fixture").Outcome);
}
