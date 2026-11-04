namespace Agwinterm.Pty.Tests;

public class AgtermQuestionDeliveryTests
{
    [Fact] public void LostAcknowledgementIsUncertainUntilExactOriginReadback()
    {
        var delivery = new AgtermQuestionDelivery(); long attempt = delivery.Begin("q");
        Assert.Equal("awaiting-origin", delivery.Outcome);
        delivery.Disconnected(); Assert.Equal("uncertain", delivery.Outcome);
        delivery.Observe(attempt, "answered"); Assert.Equal("answered", delivery.Outcome);
        delivery.Disconnected(); delivery.Observe(attempt, "uncertain");
        Assert.Equal("answered", delivery.Outcome);
    }
    [Fact] public void LateReadbackCannotCompleteAnotherAttemptEvenWithReusedId()
    {
        var delivery = new AgtermQuestionDelivery(); long old = delivery.Begin("same-id");
        delivery.Disconnected(); long current = delivery.Begin("same-id");
        delivery.Observe(old, "answered"); Assert.Equal("awaiting-origin", delivery.Outcome);
        delivery.Observe(current, "cancelled"); Assert.Equal("cancelled", delivery.Outcome);
        delivery.Observe(old, "uncertain"); Assert.Equal("cancelled", delivery.Outcome);
    }
}
