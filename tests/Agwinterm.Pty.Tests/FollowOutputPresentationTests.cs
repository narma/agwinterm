using System.Text;

namespace Agwinterm.Pty.Tests;

public class FollowOutputPresentationTests
{
    [Fact]
    public void LocalAdapterAcknowledgesTrackedOffsetWithoutTreatingItAsUserScroll()
    {
        using var session = InProcessSessionBackend.Instance.Create("scroll-test", 30, 5);
        var presentation = session.Presentation!;
        session.Emulator.Feed(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("old\r\n", 30))));
        using var before = presentation.Capture(10, 8, 16)!;
        session.Emulator.Feed("one\r\ntwo\r\n"u8);
        using var after = presentation.Capture(10, 8, 16)!;
        Assert.Equal(12, after.State.ScrollOffset);
        session.Emulator.Feed("three\r\n"u8);
        using var acknowledged = presentation.Capture(12, 8, 16)!;
        Assert.Equal(13, acknowledged.State.ScrollOffset);
        Assert.Equal(after.Identity.ViewportRevision, acknowledged.Identity.ViewportRevision);
        using var userScroll = presentation.Capture(10, 8, 16)!;
        Assert.Equal(10, userScroll.State.ScrollOffset);
        Assert.True(userScroll.Identity.ViewportRevision > acknowledged.Identity.ViewportRevision);
    }

    [Fact]
    public void SynchronizedOutputDoesNotOverwriteNewScrollWithAnOldFrame()
    {
        using var session = InProcessSessionBackend.Instance.Create("scroll-test", 30, 5);
        var presentation = session.Presentation!;
        session.Emulator.Feed(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("old\r\n", 30))));
        using var before = presentation.Capture(10, 8, 16)!;
        session.Emulator.Feed("\x1b[?2026hnew\r\n"u8);
        Assert.Null(presentation.Capture(0, 8, 16));
        session.Emulator.Feed("\x1b[?2026l"u8);
        using var after = presentation.Capture(0, 8, 16)!;
        Assert.Equal(0, after.State.ScrollOffset);
    }
}
