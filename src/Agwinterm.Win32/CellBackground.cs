using Vortice;
using Vortice.Direct2D1;

namespace Agwinterm.Win32;

internal static class CellBackground
{
    internal static void Fill(ID2D1RenderTarget target, ID2D1Brush brush,
        float ox, float oy, float cw, float ch, int row, int from, int end)
    {
        // Adjacent cells must share an opaque device-pixel edge. Per-primitive
        // antialiasing blends each rectangle separately and leaves dark seams
        // at fractional DPI coordinates. Text antialiasing is independent.
        var previous = target.AntialiasMode;
        target.AntialiasMode = AntialiasMode.Aliased;
        try
        {
            target.FillRectangle(new RawRectF(ox + from * cw, oy + row * ch,
                ox + end * cw, oy + (row + 1) * ch), brush);
        }
        finally { target.AntialiasMode = previous; }
    }
}
