using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class AgtermPageDocumentTests
{
    [Fact]
    public void OriginReloadRejectsOldDocumentAndActivationAcrossChannels()
    {
        var fence = new AgtermPageDocument();
        var old = new AgtermPageDocumentRequest(1, 0, null);
        fence.Activate(old, 0);
        fence.RequireCurrent(old);
        fence.Invalidate(3);
        Assert.Throws<IOException>(() => fence.RequireCurrent(old));
        Assert.Throws<IOException>(() => fence.Activate(new(99, 2, null), 3));
        Assert.Throws<IOException>(() => fence.Activate(new(2, 4, null), 3));
        var current = new AgtermPageDocumentRequest(2, 3, null);
        fence.Activate(current, 3);
        fence.RequireCurrent(current);
        Assert.Throws<IOException>(() => fence.RequireCurrent(old));
    }

    [Fact]
    public void SelfNavigationInvalidatesAlreadyQueuedCommandsAndCannotReactivateOldDocument()
    {
        var fence = new AgtermPageDocument();
        var old = new AgtermPageDocumentRequest(1, 0, null);
        fence.Activate(old, 0);
        fence.Activate(new(2, 0, null), 0);
        Assert.Throws<IOException>(() => fence.RequireCurrent(old));
        Assert.Throws<IOException>(() => fence.Activate(old, 0));
        Assert.Throws<IOException>(() => fence.Activate(new(2, 0, null), 0));
    }

    [Theory]
    [InlineData("{\"cmd\":\"session.overlay.submit\",\"args\":{\"value\":\"old\"}}")]
    [InlineData("{\"document\":0,\"appliedSeq\":0}")]
    [InlineData("{\"document\":1,\"appliedSeq\":-1}")]
    [InlineData("{\"document\":9007199254740992,\"appliedSeq\":0}")]
    [InlineData("{\"document\":1,\"document\":2,\"appliedSeq\":0}")]
    [InlineData("{\"document\":1,\"appliedSeq\":0,\"target\":\"other\"}")]
    [InlineData("{\"document\":1,\"appliedSeq\":0,\"request\":null}")]
    public void RejectsLegacyOrMalformedEnvelope(string json) => Assert.Throws<IOException>(() => AgtermPageDocumentRequest.Parse(json));

    [Fact]
    public void EnvelopeRoundTripsWithoutChangingControlRequest()
    {
        const string command = "{\"cmd\":\"session.overlay.submit\",\"args\":{\"value\":\"answer\"}}";
        var request = new AgtermPageDocumentRequest(7, 4, command);
        Assert.Equal(request, AgtermPageDocumentRequest.Parse(request.Encode()));
        var activation = new AgtermPageDocumentRequest(8, 4, null);
        Assert.Equal(activation, AgtermPageDocumentRequest.Parse(activation.Encode()));
    }
}
