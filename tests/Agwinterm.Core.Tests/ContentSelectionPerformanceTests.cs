namespace Agwinterm.Core.Tests;

public class ContentSelectionPerformanceTests
{
    [Fact]
    public void RepeatedRowsAndChangingStatusAllocateNothingAfterCapture()
    {
        var screen = new ScreenBuffer(240, 100);
        for (int row = 0; row < screen.Rows; row++)
            screen.FillRow(row, Cell.Empty with { Rune = 'x' });
        var selection = new ContentSelection(screen, 0, 0, 99, 239, false);
        for (int i = 0; i < 20; i++) Tick(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Tick(i);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);

        void Tick(int i)
        {
            screen[99, 239] = Cell.Empty with { Rune = i % 2 == 0 ? '!' : '?' };
            selection.TryLocate(screen, out _);
        }
    }

    [Fact]
    public void LazyTextStillUsesOriginalSnapshotAfterFrameChanges()
    {
        var screen = new ScreenBuffer(6, 3);
        screen[1, 0] = Cell.Empty with { Rune = 'A' };
        screen[1, 1] = Cell.Empty with { Rune = 0x1f600, Width = 2 };
        screen[1, 2] = Cell.Empty with { Rune = 0, Width = 0 };
        var selection = new ContentSelection(screen, 1, 0, 1, 5, false);
        screen.Clear();
        Assert.False(selection.TryLocate(screen, out _));
        Assert.False(selection.TryLocate(screen, out _));
        Assert.Equal("A😀", selection.Text);
        Assert.Same(selection.Text, selection.Text);
    }

    [Fact]
    public void MissingWordReturnsAtItsNewLocationAfterNegativeCache()
    {
        var screen = new ScreenBuffer(8, 4);
        screen[1, 0] = Cell.Empty with { Rune = 'A' };
        var selection = new ContentSelection(screen, 1, 0, 1, 0, false);
        screen.Clear();
        for (int i = 0; i < 3; i++) Assert.False(selection.TryLocate(screen, out _));
        screen[2, 0] = Cell.Empty with { Rune = 'A' };
        Assert.True(selection.TryLocate(screen, out int row));
        Assert.Equal(2, row);
    }

    [Fact]
    public void ResizeInvalidatesCachedResultEvenWhenTextReturnsUnchanged()
    {
        var screen = new ScreenBuffer(8, 4);
        screen[1, 0] = Cell.Empty with { Rune = 'A' };
        var selection = new ContentSelection(screen, 1, 0, 1, 0, false);
        screen.Resize(7, 4);
        Assert.False(selection.TryLocate(screen, out _));
        screen.Resize(8, 4);
        Assert.True(selection.TryLocate(screen, out int row));
        Assert.Equal(1, row);
        screen.Resize(8, 6);
        Assert.True(selection.TryLocate(screen, out row));
        Assert.Equal(1, row);
    }

    [Fact]
    public void EqualSpansAtDifferentColumnsAreNotInterchanged()
    {
        var screen = new ScreenBuffer(8, 5);
        screen[1, 6] = Cell.Empty with { Rune = 'A' };
        screen[1, 7] = Cell.Empty with { Rune = 'B' };
        screen[2, 0] = Cell.Empty with { Rune = 'A' };
        screen[2, 1] = Cell.Empty with { Rune = 'B' };
        var selection = new ContentSelection(screen, 1, 6, 2, 1, false);
        screen.MoveRows(1, 0, 4);
        screen.FillRow(4, Cell.Empty);
        Assert.True(selection.TryLocate(screen, out int row));
        Assert.Equal(0, row);
        Assert.Equal("AB\r\nAB", selection.Text);
    }
}
