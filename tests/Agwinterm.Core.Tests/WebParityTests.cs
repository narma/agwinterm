using System.Buffers.Binary;
using Xunit;

namespace Agwinterm.Core.Tests;

public class WebParityTests
{
    [Theory]
    [InlineData("session.type", true)]
    [InlineData("session.status", true)]
    [InlineData("surface.cursor", true)]
    [InlineData("session.rename", false)]
    [InlineData("session.close", false)]
    [InlineData("session.overlay", false)]
    public void BridgeSeparatesPaneAndSessionTargets(string command, bool pane)
        => Assert.Equal(pane, WebBridgeRequest.TargetsPane(command));

    [Theory]
    [InlineData("zmx.present")]
    [InlineData("zmx.reset")]
    [InlineData("session.overlay.job.run")]
    [InlineData("session.overlay.page.serve")]
    [InlineData("session.companion.page.attach")]
    public void GeneralBridgeRefusesUpstreamExcludedCommands(string command)
        => Assert.Throws<ArgumentException>(() => WebBridgeRequest.Parse(System.Text.Json.JsonSerializer.Serialize(new { id = "1", token = "secret", method = command, options = new { } }), "secret"));

    [Fact]
    public void GeneralBridgePreservesExplicitContextAndArguments()
    {
        var request = WebBridgeRequest.Parse("""{"id":"1","token":"secret","method":"session.rename","options":{"target":"other","window":"window","args":{"name":"Report"}}}""", "secret");
        Assert.Equal("other", request.Target); Assert.Equal("window", request.Window);
        Assert.Equal("Report", request.Args.GetProperty("name").GetString());
        Assert.Throws<ArgumentException>(() => WebBridgeRequest.Parse("""{"id":"1","token":"old","method":"session.close"}""", "secret"));
        Assert.Throws<ArgumentException>(() => WebBridgeRequest.Parse("""{"id":"1","token":"secret","method":"session.close","options":{"args":{"x":1,"x":2}}}""", "secret"));
    }

    [Fact]
    public void ImageDimensionsAndLimitsAreCheckedBeforeBrowserAllocation()
    {
        byte[] png = new byte[24]; new byte[] {137,80,78,71,13,10,26,10}.CopyTo(png,0); "IHDR"u8.CopyTo(png.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), 320); BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), 240);
        Assert.Equal(new WebImageInfo("image/png", 320, 240), WebImageInfo.Parse(png));
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), 100_001); BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), 1000);
        Assert.Throws<ArgumentException>(() => WebImageInfo.Parse(png));
        Assert.Throws<ArgumentException>(() => WebImageInfo.Parse("not an image"u8));
        Assert.Throws<ArgumentException>(() => WebImageInfo.Parse("GIF89a"u8));
        Assert.Throws<ArgumentException>(() => WebImageInfo.Parse(new byte[WebImageInfo.MaxBytes + 1]));
    }
}
