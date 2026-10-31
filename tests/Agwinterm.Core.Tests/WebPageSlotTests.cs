namespace Agwinterm.Core.Tests;

public class WebPageSlotTests
{
    private static WebPageState Page() => new(new OverlayGeometry(100));
    [Fact]
    public void ProgramAndWebOccupancyRefuseWithoutReplacingTheReservation()
    {
        var slots = new WebPageSlots<string, WebPageState>(); var page = Page();
        Assert.False(slots.TryReserve("owner", page, true, true));
        Assert.Equal(0, slots.Count);
        Assert.True(slots.TryReserve("owner", page, true, false));
        Assert.False(slots.TryReserve("owner", Page(), true, false));
        Assert.Same(page, slots.GetValueOrDefault("owner"));
        Assert.True(slots.ContainsKey("owner")); // Native program admission checks this same slot.
    }
    [Fact]
    public void HudReplacementWaitsForSuccessfulPresentationCommit()
    {
        var slots = new WebPageSlots<string, WebPageState>(); var page = Page(); bool hud = true;
        Assert.False(slots.TryReserve("owner", page, false, false));
        Assert.False(slots.CommitPresentation("owner", page, () => hud = false));
        Assert.True(hud);
        Assert.True(slots.TryReserve("owner", page, true, false));
        Assert.True(hud);
        Assert.True(slots.CommitPresentation("owner", page, () => hud = false));
        Assert.False(hud);
    }
    [Fact]
    public void ConcurrentValidatedOpensHaveOneReservationWinner()
    {
        var slots = new WebPageSlots<string, WebPageState>(); int wins = 0;
        Parallel.For(0, 100, _ => { if (slots.TryReserve("owner", Page(), true, false)) Interlocked.Increment(ref wins); });
        Assert.Equal(1, wins); Assert.Equal(1, slots.Count);
    }
    [Fact]
    public void OwnerRemovalAndReplacementInvalidateLatePresentation()
    {
        var slots = new WebPageSlots<string, WebPageState>(); var old = Page(); var replacement = Page(); bool changed = false;
        Assert.True(slots.TryReserve("owner", old, true, false));
        Assert.True(slots.Remove("owner", out var removed)); Assert.Same(old, removed);
        Assert.False(slots.CommitPresentation("owner", old, () => changed = true));
        Assert.True(slots.TryReserve("owner", replacement, true, false));
        Assert.False(slots.CommitPresentation("owner", old, () => changed = true));
        Assert.False(changed); Assert.Same(replacement, slots.GetValueOrDefault("owner"));
    }
}
