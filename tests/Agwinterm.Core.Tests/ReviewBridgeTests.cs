using System.Text.Json;
using Xunit;

namespace Agwinterm.Core.Tests;

public class ReviewBridgeTests
{
    private static string Message(object options, string method = "session.overlay.submit", string token = "live")
        => JsonSerializer.Serialize(new { id = "1", token, method, options });

    [Fact]
    public void EmptyStringSubmissionIsNotCancellation()
    {
        var result = ReviewBridgeRequest.Parse(Message(new { args = new { value = "" } }), "live");
        Assert.Equal("", result.Value);
        Assert.Null(ReviewBridgeRequest.Parse(Message(new { }, "session.overlay.close"), "live").Value);
    }
    [Fact]
    public void OverridesUnknownCommandsAndStaleDocumentsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => ReviewBridgeRequest.Parse(Message(new { target = "other", args = new { value = "x" } }), "live"));
        Assert.Throws<ArgumentException>(() => ReviewBridgeRequest.Parse(Message(new { window = "other" }, "session.overlay.close"), "live"));
        Assert.Throws<ArgumentException>(() => ReviewBridgeRequest.Parse(Message(new { args = new { value = "x" } }, "session.type"), "live"));
        Assert.Throws<ArgumentException>(() => ReviewBridgeRequest.Parse(Message(new { args = new { value = "x" } }, token: "old"), "live"));
        Assert.Throws<ArgumentException>(() => ReviewBridgeRequest.Parse(Message(new { args = new { value = 42 } }), "live"));
    }
    [Fact]
    public void LimitCountsUtf8BytesNotCharacters()
    {
        string fits = new('a', ReviewBridgeRequest.MaxValueBytes);
        Assert.Equal(fits, ReviewBridgeRequest.Parse(Message(new { args = new { value = fits } }), "live").Value);
        Assert.Throws<ArgumentException>(() => ReviewBridgeRequest.Parse(Message(new { args = new { value = fits + "x" } }), "live"));
        Assert.Throws<ArgumentException>(() => ReviewBridgeRequest.Parse(Message(new { args = new { value = new string('Ж', ReviewBridgeRequest.MaxValueBytes / 2 + 1) } }), "live"));
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("{\"id\":\"1\",\"id\":\"2\",\"token\":\"live\",\"method\":\"session.overlay.close\",\"options\":{}}")]
    [InlineData("{\"id\":\"1\",\"token\":\"live\",\"method\":\"session.overlay.close\",\"options\":{\"args\":{\"value\":\"x\"}}}")]
    public void MalformedOrConflictingShapesRefuse(string json) => Assert.Throws<ArgumentException>(() => ReviewBridgeRequest.Parse(json, "live"));

    [Fact]
    public void BrokenJsonAndOversizedEnvelopesRefuseBeforeDispatch()
    {
        Assert.ThrowsAny<JsonException>(() => ReviewBridgeRequest.Parse("{", "live"));
        Assert.Throws<ArgumentException>(() => ReviewBridgeRequest.Parse(new string(' ', 6 * ReviewBridgeRequest.MaxValueBytes + 4097), "live"));
    }
}
