using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Agwinterm.Win32;

/// <summary>UI-thread-only, device-bound raster reuse for fitted fallback glyphs.</summary>
internal sealed class SoloGlyphBitmapCache : IDisposable
{
    private const int Capacity = 256;
    private const long ByteLimit = 16 * 1024 * 1024;
    // Glyph has reference identity. Include foreground because some color fonts contain
    // foreground-colored layers; include subpixel position to preserve grayscale AA.
    private readonly record struct Key(SoloGlyphCache.Glyph Glyph, Color4 Color,
        float Width, float Height, float DpiX, float DpiY, float PhaseX, float PhaseY);
    private sealed record Entry(Key Key, ID2D1Bitmap Bitmap, long Bytes);
    private readonly Dictionary<Key, LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _recent = new();
    private readonly Dictionary<Key, LinkedListNode<Key>> _seen = new();
    private readonly LinkedList<Key> _seenOrder = new();
    private long _bytes;
    internal int Count => _entries.Count;
    internal long Bytes => _bytes;
    internal int Hits { get; private set; }
    internal int Misses { get; private set; }

    internal void Draw(ID2D1RenderTarget target, ID2D1SolidColorBrush brush,
        SoloGlyphCache.Glyph glyph, float x, float y, float width, float height)
    {
        if (!glyph.HasInk) return;
        // The terminal paints in target DIPs. Non-identity transforms need the direct
        // path rather than rasterizing with an incomplete transform in the cache key.
        if (target.Transform != Matrix3x2.Identity) { DrawDirect(target, brush, glyph, x, y, width, height); return; }
        var dpi = target.Dpi;
        float sx = dpi.Width / 96f, sy = dpi.Height / 96f;
        float px = x * sx, py = y * sy;
        float floorX = MathF.Floor(px), floorY = MathF.Floor(py);
        float phaseX = px - floorX, phaseY = py - floorY;
        int pixelWidth = (int)MathF.Ceiling(width * sx + phaseX);
        int pixelHeight = (int)MathF.Ceiling(height * sy + phaseY);
        long bytes = (long)pixelWidth * pixelHeight * 4;
        if (pixelWidth <= 0 || pixelHeight <= 0 || bytes > ByteLimit)
        { DrawDirect(target, brush, glyph, x, y, width, height); return; }
        var key = new Key(glyph, brush.Color, width, height, dpi.Width, dpi.Height, phaseX, phaseY);
        if (_entries.TryGetValue(key, out var node))
        {
            Hits++;
            _recent.Remove(node); _recent.AddFirst(node);
        }
        else
        {
            // One-off glyphs and streams exceeding the layout LRU should pay only
            // the original draw cost, not create a throwaway offscreen surface.
            if (!_seen.Remove(key, out var seen))
            {
                if (_seen.Count == Capacity)
                { _seen.Remove(_seenOrder.Last!.Value); _seenOrder.RemoveLast(); }
                _seen.Add(key, _seenOrder.AddFirst(key));
                DrawDirect(target, brush, glyph, x, y, width, height);
                return;
            }
            _seenOrder.Remove(seen);
            Misses++;
            while (_entries.Count >= Capacity || _bytes + bytes > ByteLimit) Evict();
            // Keep exactly the parent's DPI: requesting only a fractional DIP size
            // makes Direct2D adjust DPI to round it to a whole number of pixels.
            using var raster = target.CreateCompatibleRenderTarget(
                new Size(pixelWidth / sx, pixelHeight / sy), new SizeI(pixelWidth, pixelHeight),
                new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.Unknown, Vortice.DCommon.AlphaMode.Premultiplied),
                CompatibleRenderTargetOptions.None);
            raster.Dpi = dpi;
            raster.TextAntialiasMode = TextAntialiasMode.Grayscale;
            using var parameters = target.TextRenderingParams;
            raster.TextRenderingParams = parameters;
            using var ink = raster.CreateSolidColorBrush(brush.Color);
            raster.BeginDraw();
            raster.Clear(new Color4(0, 0, 0, 0));
            DrawDirect(raster, ink, glyph, phaseX / sx, phaseY / sy, width, height);
            raster.EndDraw().CheckError();
            // Bitmap is the wrapper's borrowed cached property. Retain our own COM
            // reference, which survives disposing the scratch target.
            var bitmap = raster.Bitmap.QueryInterface<ID2D1Bitmap>();
            node = _recent.AddFirst(new Entry(key, bitmap, bytes));
            _entries.Add(key, node); _bytes += bytes;
        }
        target.PushAxisAlignedClip(new RawRectF(x, y, x + width, y + height), AntialiasMode.Aliased);
        try
        {
            float left = floorX / sx, top = floorY / sy;
            target.DrawBitmap(node.Value.Bitmap,
                new RawRectF(left, top, left + pixelWidth / sx, top + pixelHeight / sy),
                1, BitmapInterpolationMode.NearestNeighbor, null);
        }
        finally { target.PopAxisAlignedClip(); }
    }

    internal static void DrawDirect(ID2D1RenderTarget target, ID2D1SolidColorBrush brush,
        SoloGlyphCache.Glyph glyph, float x, float y, float width, float height)
    {
        if (!glyph.HasInk) return;
        var transform = target.Transform;
        target.PushAxisAlignedClip(new RawRectF(x, y, x + width, y + height), AntialiasMode.Aliased);
        try
        {
            if (glyph.Scale == 1f)
                target.DrawTextLayout(new Vector2(x + glyph.Dx, y + glyph.Dy), glyph.Layout, brush, DrawTextOptions.EnableColorFont);
            else
            {
                target.Transform = Matrix3x2.CreateScale(glyph.Scale)
                    * Matrix3x2.CreateTranslation(x + glyph.Dx, y + glyph.Dy) * transform;
                target.DrawTextLayout(Vector2.Zero, glyph.Layout, brush, DrawTextOptions.EnableColorFont);
            }
        }
        finally
        {
            if (glyph.Scale != 1f) target.Transform = transform;
            target.PopAxisAlignedClip();
        }
    }

    private void Evict()
    {
        var oldest = _recent.Last!;
        _entries.Remove(oldest.Value.Key); _recent.RemoveLast();
        _bytes -= oldest.Value.Bytes; oldest.Value.Bitmap.Dispose();
    }
    internal void Clear()
    {
        while (_recent.Count != 0) Evict();
        _seen.Clear(); _seenOrder.Clear();
        Hits = Misses = 0;
    }
    public void Dispose() => Clear();
}
