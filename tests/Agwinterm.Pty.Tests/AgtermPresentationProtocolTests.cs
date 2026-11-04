using System.Text;
using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class AgtermPresentationProtocolTests
{
    [Fact]
    public void CapturedRealCliFramesRoundtripWithoutChangingWirePayloads()
    {
        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Presentation", "pinned-frames.json")));
        var kinds = new HashSet<string>();
        foreach (var entry in capture.RootElement.EnumerateArray())
        {
            if (entry.GetProperty("channel").GetString() != "presentation") continue;
            var raw = entry.GetProperty("frame");
            var frame = AgtermPresentationProtocol.Decode(JsonSerializer.SerializeToUtf8Bytes(raw));
            kinds.Add(frame.Kind);
            var encoded = AgtermPresentationProtocol.Encode(frame);
            using var roundtrip = JsonDocument.Parse(encoded.AsMemory()[..^1]);
            Assert.True(JsonElement.DeepEquals(raw, roundtrip.RootElement));
        }
        Assert.Subset(kinds, new HashSet<string> { "hello", "snapshot", "status", "context", "ask.request",
            "ask.resolve", "ask.dismiss", "overlay.request", "page.request" });
    }

    private static AgtermPresentationFrame Decode(string text) => AgtermPresentationProtocol.Decode(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData("{\"kind\":\"ping\",\"gen\":1,\"rev\":0,\"rev\":1}")]
    [InlineData("{\"kind\":\"snapshot\",\"gen\":1,\"rev\":1,\"snapshot\":{\"a\":1,\"a\":2}}")]
    [InlineData("{\"kind\":\"ping\",\"gen\":-1,\"rev\":0}")]
    [InlineData("{\"kind\":\"ping\",\"gen\":1}")]
    [InlineData("{\"kind\":\"ask.request\",\"gen\":1,\"rev\":0}")]
    [InlineData("{\"kind\":\"context\",\"gen\":1,\"rev\":0,\"context\":false}")]
    public void InvalidEnvelopesRefuseBeforeEffects(string text) => Assert.Throws<IOException>(() => Decode(text));

    [Fact]
    public void UnknownKindPreservesOrderingWithoutExposingPayload()
    {
        var frame = Decode("{\"kind\":\"future\",\"gen\":5,\"rev\":123,\"execute\":\"something\"}");
        Assert.Equal(5, frame.Generation);
        Assert.Equal(123, frame.Revision);
        Assert.Equal(JsonValueKind.Undefined, frame.Body.ValueKind);
    }

    [Fact]
    public void IncrementalReadsHandleUnicodeAndMultipleFrames()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"kind\":\"context\",\"gen\":1,\"rev\":1,\"context\":\"界\"}\n{\"kind\":\"status\",\"gen\":1,\"rev\":2}\n");
        var lines = new AgtermPresentationLines();
        var frames = new List<AgtermPresentationFrame>();
        foreach (byte b in bytes) lines.Feed([b], frames.Add);
        lines.Complete();
        Assert.Equal(2, frames.Count);
        Assert.Equal("界", frames[0].Body.GetString());
        Assert.Equal(JsonValueKind.Undefined, frames[1].Body.ValueKind);
        Assert.Equal(frames[0].Kind, AgtermPresentationProtocol.Decode(AgtermPresentationProtocol.Encode(frames[0]).AsMemory()[..^1]).Kind);
    }

    [Fact]
    public void UnterminatedOversizeAndInvalidUtf8AreTerminalFailures()
    {
        var lines = new AgtermPresentationLines();
        lines.Feed("{"u8, _ => Assert.Fail());
        Assert.Throws<IOException>(lines.Complete);
        Assert.Throws<IOException>(() => lines.Feed("}"u8, _ => Assert.Fail()));
        var large = new AgtermPresentationLines();
        Assert.Throws<IOException>(() => large.Feed(new byte[AgtermPresentationProtocol.MaxFrameBytes + 1], _ => Assert.Fail()));
        Assert.Throws<IOException>(() => AgtermPresentationProtocol.Decode(new byte[] { 0xff }));
    }

    [Fact]
    public void NegotiationNeverAdvertisesUnimplementedExtensions()
    {
        var hello = AgtermPresentationProtocol.Hello(Decode("{\"kind\":\"hello\",\"gen\":1,\"rev\":0,\"hello\":{\"version\":2,\"mode\":\"presenter\",\"kinds\":[\"status\",\"ask.form\",\"page.request\"]}}"));
        Assert.Equal(1, AgtermPresentationProtocol.NegotiateVersion(hello.Version));
        Assert.Null(AgtermPresentationProtocol.NegotiateVersion(0));
        Assert.Null(AgtermPresentationProtocol.NegotiateVersion(-1));
        Assert.Equal(new[] { "status" }, AgtermPresentationProtocol.Intersect(hello, ["status", "hud"]));
    }
}
