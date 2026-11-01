using Xunit;

namespace Agwinterm.Core.Tests;

public class WebPageTests
{
    [Fact]
    public void EmptySubmissionWinsOverClose()
    {
        var outcomes = new WebPageOutcomes(); outcomes.Register("review");
        Assert.True(outcomes.Complete("review", ""));
        Assert.False(outcomes.Complete("review", null));
        Assert.Equal(new WebPageOutcome("review", "submitted", ""), outcomes.Read("review"));
    }
    [Fact]
    public void PendingPagesSurviveFinishedEviction()
    {
        var outcomes = new WebPageOutcomes(); outcomes.Register("pending");
        for (int i = 0; i < 33; i++) { outcomes.Register(i.ToString()); outcomes.Complete(i.ToString(), null); }
        Assert.Throws<ArgumentException>(() => outcomes.Read("0"));
        Assert.Equal("dismissed", outcomes.Read("1").Outcome);
        Assert.Equal("pending", outcomes.Read("pending").Outcome);
    }
    [Fact]
    public void ConcurrentCompletionHasOnlyOneWinner()
    {
        var outcomes = new WebPageOutcomes(); outcomes.Register("race"); int wins = 0;
        Parallel.For(0, 50, n => { if (outcomes.Complete("race", n % 2 == 0 ? null : "note")) Interlocked.Increment(ref wins); });
        Assert.Equal(1, wins);
        Assert.NotEqual("pending", outcomes.Read("race").Outcome);
    }
    [Fact]
    public void ReloadRetainsPageIdentityAndGeometryButInvalidatesDocument()
    {
        var page = new WebPageState(new(60, 400)); var id = page.Id;
        page.BeginDocument(); long generation = page.DocumentGeneration;
        page.Hidden = true; page.BeginDocument();
        Assert.Equal(id, page.Id); Assert.Equal(new OverlayGeometry(60, 400), page.Geometry);
        Assert.True(page.Hidden); Assert.True(page.DocumentGeneration > generation);
    }
}
