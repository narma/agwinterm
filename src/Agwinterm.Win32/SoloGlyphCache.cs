using Vortice.DirectWrite;

namespace Agwinterm.Win32;

/// <summary>UI-thread-only, bounded LRU of device-independent solo layouts and their ink fit.</summary>
internal sealed class SoloGlyphCache : IDisposable
{
    private const int Capacity = 256;
    // Format identity includes family, size and style. Width also distinguishes the optional
    // neighbouring blank cell. Position and foreground colour do not affect layout or ink fit.
    private readonly record struct Key(int Rune, IDWriteTextFormat Format, float Width, float Height);
    private readonly Dictionary<Key, LinkedListNode<(Key Key, Glyph Value)>> _entries = new();
    private readonly LinkedList<(Key Key, Glyph Value)> _recent = new();
    internal int Count => _entries.Count;

    internal sealed record Glyph(IDWriteTextLayout Layout, float Scale, float Dx, float Dy, bool HasInk);

    internal Glyph Get(IDWriteFactory factory, int rune, IDWriteTextFormat format, float width, float height)
    {
        var key = new Key(rune, format, width, height);
        if (_entries.TryGetValue(key, out var node))
        {
            _recent.Remove(node);
            _recent.AddFirst(node);
            return node.Value.Value;
        }
        var glyph = Create(factory, rune, format, width, height);
        if (_entries.Count == Capacity)
        {
            var oldest = _recent.Last!;
            _entries.Remove(oldest.Value.Key);
            _recent.RemoveLast();
            oldest.Value.Value.Layout.Dispose();
        }
        _entries.Add(key, _recent.AddFirst((key, glyph)));
        return glyph;
    }

    internal static Glyph Create(IDWriteFactory factory, int rune, IDWriteTextFormat format, float width, float height)
    {
        string text = char.ConvertFromUtf32(rune);
        var layout = factory.CreateTextLayout(text, format, width, height);
        try
        {
            // Preserve the original fit exactly: fallback ink may exceed the terminal baseline.
            var overhang = layout.OverhangMetrics;
            float left = -overhang.Left, top = -overhang.Top;
            float right = width + overhang.Right, bottom = height + overhang.Bottom;
            float inkWidth = right - left, inkHeight = bottom - top;
            if (inkWidth <= 0 || inkHeight <= 0) return new(layout, 1f, 0f, 0f, false);
            float padding = MathF.Min(0.5f, MathF.Min(width, height) * 0.1f);
            float scale = MathF.Min(1f, MathF.Min((width - 2 * padding) / inkWidth, (height - 2 * padding) / inkHeight));
            float minX = padding - left * scale, minY = padding - top * scale;
            float dx = Math.Clamp(0f, minX, MathF.Max(minX, width - padding - right * scale));
            float dy = Math.Clamp(0f, minY, MathF.Max(minY, height - padding - bottom * scale));
            return new(layout, scale, dx, dy, true);
        }
        catch { layout.Dispose(); throw; }
    }

    internal void Clear()
    {
        foreach (var entry in _recent) entry.Value.Layout.Dispose();
        _entries.Clear();
        _recent.Clear();
    }

    public void Dispose() => Clear();
}
