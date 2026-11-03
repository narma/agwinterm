using System.Text.Json;
using Agwinterm.Core;

namespace Agwinterm.Pty.Tests;

public class AgtermPassivePresentationTests
{
    private readonly Guid _origin = Guid.NewGuid();
    private Dictionary<Guid, string> Mapping => new() { [_origin] = "local-pane" };
    private static AgtermPresentationFrame Frame(string kind, object? payload) => new(kind, 1, 1, JsonSerializer.SerializeToElement(payload));

    [Fact] public void SnapshotClearsWithoutReplayingNotifications()
    {
        var state = new AgtermPassivePresentation();
        state.Apply(Frame("status", new { status = "blocked", blink = true, pane = _origin, changedAt = 123.4 }), Mapping, 0);
        state.Apply(Frame("context", "task"), Mapping, 0);
        Assert.Equal("local-pane", state.Status!.Pane); Assert.Equal(AgentStatus.Blocked, state.Status.Status);
        Assert.Null(state.Apply(Frame("snapshot", new { notify = new { title = "old", body = "old" } }), Mapping, 0));
        Assert.Null(state.Status); Assert.Null(state.Context);
    }

    [Fact] public void InvalidReplacementDoesNotPartiallyCommit()
    {
        var state = new AgtermPassivePresentation();
        state.Apply(Frame("context", "keep"), Mapping, 0);
        Assert.Throws<IOException>(() => state.Apply(Frame("snapshot", new { context = "replace", status = new { status = "blocked", blink = true, pane = Guid.NewGuid() } }), Mapping, 0));
        Assert.Equal("keep", state.Context);
    }

    [Fact] public void LayoutSwapPreservesIdentityButRemovalInvalidates()
    {
        var state = new AgtermPassivePresentation(); var other = Guid.NewGuid();
        var mapping = Mapping; mapping.Add(other, "other-local");
        state.Apply(Frame("layout", new { panes = new[] { other, _origin }, primary = other, axis = "vertical", shown = true }), mapping, 0);
        state.Apply(Frame("status", new { status = "active", blink = false, pane = _origin }), mapping, 0);
        Assert.Equal("local-pane", state.Status!.Pane);
        Assert.Throws<IOException>(() => state.Apply(Frame("layout", new { panes = new[] { other }, primary = other, shown = false }), mapping, 0));
    }

    [Fact] public void HudUsesRemainingLifetimeAndRejectsOldExpiry()
    {
        var state = new AgtermPassivePresentation();
        state.Apply(Frame("hud", new { spec = new { message = "old", hideAfter = 30 }, generation = 1, remaining = 2.5, pane = _origin }), Mapping, 1000);
        Assert.Equal(3500, state.HudDeadline); Assert.Equal("local-pane", state.Hud!.AnchorPaneId);
        state.Apply(Frame("hud", new { spec = new { message = "new", hideAfter = 30 }, generation = 2, remaining = 4 }), Mapping, 2000);
        Assert.False(state.ExpireHud(6000, 1)); Assert.False(state.ExpireHud(5999, 2)); Assert.True(state.ExpireHud(6000, 2));
        Assert.Null(state.Hud);
    }

    [Fact] public void TimedHudCannotRestartFullLifetimeWithoutRemaining()
    {
        var state = new AgtermPassivePresentation();
        Assert.Throws<IOException>(() => state.Apply(Frame("hud", new { spec = new { message = "x", hideAfter = 30 }, generation = 1 }), Mapping, 0));
    }
    [Fact] public void UnsupportedHudGeometryIsRefusedInsteadOfSilentlyClamped()
    {
        var state = new AgtermPassivePresentation();
        Assert.Throws<IOException>(() => state.Apply(Frame("hud", new { spec = new { message = "x", sizePercent = 100 }, generation = 1 }), Mapping, 0));
        Assert.Null(state.Hud);
    }

    [Theory] [InlineData("scratch")] [InlineData("active")] [InlineData("12345678-1234-1234-1234-123456789abc")]
    public void UnknownNotificationPaneNeverFallsBackToActive(string pane)
    {
        var state = new AgtermPassivePresentation();
        Assert.Throws<IOException>(() => state.Apply(Frame("notify", new { title = "x", body = "y", pane }), Mapping, 0));
    }
}
