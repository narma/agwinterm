using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

/// <summary>Host image pixel conversion used by the Direct2D upload path.</summary>
public class KittyPixelFormatTests
{
    [Fact]
    public void Upload_Bgra_TakesTheNoSwizzleRoute()
    {
        // One opaque pixel, already in target order: B=10, G=20, R=30, A=255. It must come out
        // byte-for-byte, not reversed.
        var img = new KittyImage(1, KittyFormat.Bgra, 1, 1, new byte[] { 10, 20, 30, 255 });
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, KittyPixels.ToPremultipliedBgra(img));
    }

    [Fact]
    public void Upload_Rgba_SwizzlesRedAndBlue()
    {
        // Same four bytes read as RGBA: R=10, G=20, B=30 -> BGRA is 30, 20, 10.
        var img = new KittyImage(1, KittyFormat.Rgba, 1, 1, new byte[] { 10, 20, 30, 255 });
        Assert.Equal(new byte[] { 30, 20, 10, 255 }, KittyPixels.ToPremultipliedBgra(img));
    }

    [Fact]
    public void Upload_BgraAndRgba_DifferExactlyByTheSwap()
    {
        var pixels = new byte[] { 1, 2, 3, 255, 40, 50, 60, 255 };
        var bgra = KittyPixels.ToPremultipliedBgra(new KittyImage(1, KittyFormat.Bgra, 2, 1, pixels));
        var rgba = KittyPixels.ToPremultipliedBgra(new KittyImage(1, KittyFormat.Rgba, 2, 1, pixels));
        for (int j = 0; j < bgra.Length; j += 4)
        {
            Assert.Equal(bgra[j], rgba[j + 2]);
            Assert.Equal(bgra[j + 1], rgba[j + 1]);
            Assert.Equal(bgra[j + 2], rgba[j]);
            Assert.Equal(bgra[j + 3], rgba[j + 3]);
        }
    }

    [Fact]
    public void Upload_Bgra_PremultipliesAlpha()
    {
        // Straight alpha in, premultiplied out — D2D's render target is Premultiplied.
        var img = new KittyImage(1, KittyFormat.Bgra, 1, 1, new byte[] { 200, 100, 50, 128 });
        Assert.Equal(new byte[] { (byte)(200 * 128 / 255), (byte)(100 * 128 / 255), (byte)(50 * 128 / 255), 128 },
            KittyPixels.ToPremultipliedBgra(img));
    }

    [Fact]
    public void Upload_Rgb_ExpandsToOpaqueBgra()
    {
        var img = new KittyImage(1, KittyFormat.Rgb, 1, 1, new byte[] { 10, 20, 30 });
        Assert.Equal(new byte[] { 30, 20, 10, 255 }, KittyPixels.ToPremultipliedBgra(img));
    }

    [Fact]
    public void Upload_ShortPayload_DoesNotReadOutOfBounds()
    {
        // A lying producer declares 4x4 and supplies one pixel: the tail stays zeroed.
        var img = new KittyImage(1, KittyFormat.Bgra, 4, 4, new byte[] { 1, 2, 3, 255 });
        var outb = KittyPixels.ToPremultipliedBgra(img);
        Assert.Equal(4 * 4 * 4, outb.Length);
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, outb[..4]);
        Assert.All(outb[4..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Upload_NegativeDimensions_ReturnEmptyRatherThanThrow()
    {
        var img = new KittyImage(1, KittyFormat.Bgra, -8, 4, new byte[] { 1, 2, 3, 255 });
        Assert.Empty(KittyPixels.ToPremultipliedBgra(img));
    }

}
