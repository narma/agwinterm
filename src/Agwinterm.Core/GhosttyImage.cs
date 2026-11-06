namespace Agwinterm.Core;

public enum GhosttyPixelFormat { Rgb, Rgba, Png, GrayAlpha, Gray, Bgra }
public enum GhosttyImageLayer { BelowBackground, BelowText, AboveText }

/// <summary>Immutable pixels identified by Ghostty's globally unique content generation.</summary>
public sealed record GhosttyImage(ulong Generation, int Width, int Height, GhosttyPixelFormat Format, ReadOnlyMemory<byte> Pixels, int PayloadLength = 0)
{
    public byte[] ToPremultipliedBgra()
    {
        int channels = Format switch { GhosttyPixelFormat.Rgb => 3, GhosttyPixelFormat.Rgba or GhosttyPixelFormat.Bgra => 4,
            GhosttyPixelFormat.GrayAlpha => 2, GhosttyPixelFormat.Gray => 1,
            _ => throw new InvalidDataException("Unsupported decoded image format.") };
        int count = checked(Width * Height);
        if (Width <= 0 || Height <= 0 || count > 16 * 1024 * 1024 || Pixels.Length != checked(count * channels))
            throw new InvalidDataException("Invalid decoded image dimensions.");
        byte[] result = GC.AllocateUninitializedArray<byte>(checked(count * 4));
        var source = Pixels.Span;
        for (int i = 0, j = 0; j < result.Length; i += channels, j += 4)
        {
            byte r = source[i], g = channels >= 3 ? source[i + 1] : r, b = channels >= 3 ? source[i + 2] : r;
            byte a = channels == 4 ? source[i + 3] : channels == 2 ? source[i + 1] : (byte)255;
            if (Format == GhosttyPixelFormat.Bgra) (r,b)=(b,r);
            result[j] = (byte)(b * a / 255); result[j + 1] = (byte)(g * a / 255);
            result[j + 2] = (byte)(r * a / 255); result[j + 3] = a;
        }
        return result;
    }
}

/// <summary>A placement from the same snapshot as the captured text and cursor.</summary>
public sealed record GhosttyImagePlacement(GhosttyImage Image, uint Id, GhosttyImageLayer Layer, int Z,
    int Col, int Row, uint OffsetX, uint OffsetY, uint Width, uint Height,
    uint SourceX, uint SourceY, uint SourceWidth, uint SourceHeight);
