using Agwinterm.Core;
using Xunit;

namespace Agwinterm.Core.Tests;

public sealed class TerminalFrameTests
{
    private static TerminalFrame Create(ulong sequence, int rune = 'A', ulong epoch = 1, ulong viewport = 0)
        => new(new("session", epoch, viewport, sequence, (long)sequence),
            new(1, 1, 0, 0, 0, 0, 0, true, 1, false, false, new(0,0,0), new(255,255,255), "", ""),
            new[] { Cell.Empty with { Rune = rune } });

    [Fact]
    public void ReplacementKeepsOutstandingReadGenerationAlive()
    {
        using var cache = new TerminalFrameCache("session", 1);
        Assert.True(cache.Publish(Create(1)));
        using var first = cache.Acquire()!;
        Assert.True(cache.Publish(Create(2, 'B')));
        using var second = cache.Acquire()!;
        Assert.Equal('A', first[0,0].Rune);
        Assert.Equal('B', second[0,0].Rune);
        Assert.Equal(1ul, first.Identity.Sequence);
        cache.Dispose();
        Assert.Equal('B', second[0,0].Rune);
        second.Dispose();
        Assert.Throws<ObjectDisposedException>(() => second[0,0]);
    }

    [Fact]
    public void StaleAttachmentAndViewportCannotReplaceCurrentFrame()
    {
        using var cache = new TerminalFrameCache("session", 2);
        using var wrongEpoch = Create(100, epoch: 1);
        Assert.False(cache.Publish(wrongEpoch));
        Assert.True(cache.Publish(Create(1, epoch: 2, viewport: 3)));
        using var staleViewport = Create(2, epoch: 2, viewport: 2);
        using var staleSequence = Create(1, epoch: 2, viewport: 4);
        Assert.False(cache.Publish(staleViewport));
        Assert.False(cache.Publish(staleSequence));
        cache.Reset("replacement", 3);
        using var late = Create(200, epoch: 2, viewport: 10);
        Assert.False(cache.Publish(late));
        Assert.Null(cache.Acquire());
    }

    [Fact]
    public void ReleasedStorageCannotBeAcquiredAgain()
    {
        var frame = Create(1);
        using var outstanding = frame.Acquire();
        frame.Dispose(); frame.Dispose();
        Assert.Throws<ObjectDisposedException>(() => frame.Acquire());
        Assert.Equal('A', outstanding[0,0].Rune);
    }

    [Fact]
    public void ViewportDimensionsAreCheckedBeforeAllocation()
    {
        var state = new TerminalFrameState(int.MaxValue, 2, 0,0,0,0,0,false,0,false,false,default,default,"","");
        Assert.Throws<ArgumentException>(() => new TerminalFrame(new("session",1,0,1,0),state,Array.Empty<Cell>()));
    }
}
