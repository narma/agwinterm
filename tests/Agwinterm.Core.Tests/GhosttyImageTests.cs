using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class GhosttyImageTests
{
    [Theory]
    [InlineData(GhosttyPixelFormat.Rgba, new byte[] { 200, 100, 50, 128 }, new byte[] { 25, 50, 100, 128 })]
    [InlineData(GhosttyPixelFormat.Rgb, new byte[] { 200, 100, 50 }, new byte[] { 50, 100, 200, 255 })]
    [InlineData(GhosttyPixelFormat.GrayAlpha, new byte[] { 100, 128 }, new byte[] { 50, 50, 50, 128 })]
    [InlineData(GhosttyPixelFormat.Gray, new byte[] { 100 }, new byte[] { 100, 100, 100, 255 })]
    public void ConvertsRawFormatsWithPremultipliedAlpha(GhosttyPixelFormat format, byte[] input, byte[] expected)
        => Assert.Equal(expected, new GhosttyImage(1, 1, 1, format, input).ToPremultipliedBgra());

    [Fact]
    public void RejectsInvalidDimensionsBeforeAllocating()
    {
        Assert.Throws<InvalidDataException>(() => new GhosttyImage(1, 1, 1, GhosttyPixelFormat.Rgba, new byte[3]).ToPremultipliedBgra());
        Assert.Throws<InvalidDataException>(() => new GhosttyImage(1, 8192, 8192, GhosttyPixelFormat.Rgba, ReadOnlyMemory<byte>.Empty).ToPremultipliedBgra());
    }
}
