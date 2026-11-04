namespace Agwinterm.Core;

public enum GhosttyPixelFormat { Rgb, Rgba, Png, GrayAlpha, Gray }
public enum GhosttyImageLayer { BelowBackground, BelowText, AboveText }

/// <summary>Immutable pixels identified by Ghostty's globally unique content generation.</summary>
public sealed record GhosttyImage(ulong Generation, int Width, int Height, GhosttyPixelFormat Format, ReadOnlyMemory<byte> Pixels);

/// <summary>A placement from the same snapshot as the captured text and cursor.</summary>
public sealed record GhosttyImagePlacement(GhosttyImage Image, uint Id, GhosttyImageLayer Layer, int Z,
    int Col, int Row, uint OffsetX, uint OffsetY, uint Width, uint Height,
    uint SourceX, uint SourceY, uint SourceWidth, uint SourceHeight);
