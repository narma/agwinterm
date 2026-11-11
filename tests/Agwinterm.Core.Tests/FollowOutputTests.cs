using System.Text;

namespace Agwinterm.Core.Tests;

public class FollowOutputTests
{
    [Fact]
    public void ConfigurationDefaultsToBottomAndAllowsLegacyMode()
    {
        Assert.False(TerminalConfig.Parse("").AlwaysFollowOutput);
        Assert.False(TerminalConfig.Parse("follow-output = bottom").AlwaysFollowOutput);
        Assert.True(TerminalConfig.Parse("follow-output = always").AlwaysFollowOutput);
        Assert.False(TerminalConfig.Parse("follow-output = invalid").AlwaysFollowOutput);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutputKeepsReadPositionAndReturningToBottomResumesFollowing(bool acknowledgeOffset)
    {
        using var core = new GhosttyTerminalCore(30, 5);
        using var frames = new GhosttyFrameSource(core, new object(), "test", 1);
        void Feed(int start, int count) => core.Feed(Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Range(start, count).Select(i => $"line {i:000}\r\n"))));
        Feed(0, 30);
        using var first = frames.Capture(10, 8, 16, 1)!;
        Feed(30, 7);
        using var next = frames.Capture(10, 8, 16, 1)!;
        Assert.NotEqual(first.State.ScrollGeneration, next.State.ScrollGeneration);
        Assert.Equal(17, next.State.ScrollOffset);
        Assert.Equal(first[0, 6], next[0, 6]);
        Assert.Equal(first[0, 7], next[0, 7]);
        Feed(37, 3);
        using var third = frames.Capture(acknowledgeOffset ? 17 : 10, 8, 16, 1)!;
        Assert.Equal(20, third.State.ScrollOffset);
        // Explicit user scrolling to the original requested offset is still honored.
        using var moved = frames.Capture(10, 8, 16, 2)!;
        Assert.Equal(10, moved.State.ScrollOffset);
        using var bottom = frames.Capture(0, 8, 16, 3)!;
        Feed(40, 4);
        using var live = frames.Capture(0, 8, 16, 3)!;
        Assert.Equal(0, live.State.ScrollOffset);
    }

    [Fact]
    public void InPlaceRedrawDoesNotMoveHistoryAndAlternateScreenHasNoScrollOffset()
    {
        using var core = new GhosttyTerminalCore(30, 5);
        using var frames = new GhosttyFrameSource(core, new object(), "test", 1);
        core.Feed(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("history\r\n", 30))));
        using var before = frames.Capture(10, 8, 16, 1)!;
        core.Feed("\x1b[Hrewritten live row"u8);
        using var redraw = frames.Capture(10, 8, 16, 1)!;
        Assert.Equal(10, redraw.State.ScrollOffset);
        for (int c = 0; c < 30; c++) Assert.Equal(before[0,c], redraw[0,c]);
        core.Feed("\x1b[?1049h"u8);
        using var alternate = frames.Capture(10, 8, 16, 1)!;
        Assert.Equal(0, alternate.State.ScrollOffset);
        core.Feed("\x1b[?1049l"u8);
        using var restored = frames.Capture(0, 8, 16, 2)!;
        Assert.Equal(0, restored.State.ScrollOffset);
    }

    [Fact]
    public void FullHistoryKeepsContentUntilItIsEvicted()
    {
        using var core = new GhosttyTerminalCore(30, 5);
        core.ScrollbackMax = 20;
        using var frames = new GhosttyFrameSource(core, new object(), "test", 1);
        // Ghostty allocates history in pages; exceed the minimum page capacity.
        for (int i = 0; i < 20000; i++) core.Feed(Encoding.UTF8.GetBytes($"line {i:00000}\r\n"));
        using var before = frames.Capture(10, 8, 16, 1)!;
        for (int i = 200; i < 203; i++) core.Feed(Encoding.UTF8.GetBytes($"line {i:000}\r\n"));
        using var after = frames.Capture(10, 8, 16, 1)!;
        for (int c = 0; c < 8; c++) Assert.Equal(before[0,c], after[0,c]);
        for (int i = 0; i < 20000; i++) core.Feed("new\r\n"u8);
        using var evicted = frames.Capture(10, 8, 16, 1)!;
        Assert.Equal(evicted.State.HistoryRows, evicted.State.ScrollOffset);
    }
}
