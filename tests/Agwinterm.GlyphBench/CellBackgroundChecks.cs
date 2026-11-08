using Agwinterm.Win32;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Vortice.WIC;

// Host-free pixel regression: real Direct2D fills into offscreen WIC bitmaps.
internal static class CellBackgroundChecks
{
    internal static void Run()
    {
        using var wic = new IWICImagingFactory();
        using var d2d = D2D1.D2D1CreateFactory<ID2D1Factory>();
        int checks = 0;
        foreach (float dpi in new[] { 96f, 120f, 144f, 168f, 192f })
        foreach (float origin in new[] { 4f, 4.3f })
        foreach (bool block in new[] { false, true })
        {
            const int width = 256, height = 256, rows = 4, columns = 6;
            const float cw = 8.3f, ch = 21f;
            using var pixels = wic.CreateBitmap(width, height, PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
            using var target = d2d.CreateWicBitmapRenderTarget(pixels, new RenderTargetProperties
            {
                Type = RenderTargetType.Software,
                PixelFormat = new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                DpiX = dpi, DpiY = dpi,
            });
            using var brush = target.CreateSolidColorBrush(new Color4(40 / 255f, 90 / 255f, 150 / 255f, 1));
            int from = block ? 2 : 0, end = block ? 4 : columns;
            byte[] Render(int tiling)
            {
                target.BeginDraw();
                target.Clear(new Color4(0.25f, 0.24f, 0.26f, 1));
                target.AntialiasMode = AntialiasMode.PerPrimitive;
                target.TextAntialiasMode = TextAntialiasMode.Grayscale;
                if (tiling != 0)
                {
                    for (int row = 0; row < rows; row++)
                    {
                        if (tiling == 1)
                            CellBackground.Fill(target, brush, origin, origin, cw, ch, row, from, end);
                        else
                            for (int col = from; col < end; col++)
                                CellBackground.Fill(target, brush, origin, origin, cw, ch, row, col, col + 1);
                    }
                    if (target.AntialiasMode != AntialiasMode.PerPrimitive || target.TextAntialiasMode != TextAntialiasMode.Grayscale)
                        throw new Exception("Cell fills changed subsequent drawing antialiasing");
                }
                else
                {
                    target.AntialiasMode = AntialiasMode.Aliased;
                    target.FillRectangle(new RawRectF(origin + from * cw, origin, origin + end * cw, origin + rows * ch), brush);
                }
                target.EndDraw().CheckError();
                var bytes = new byte[width * height * 4];
                pixels.CopyPixels(width * 4, bytes);
                return bytes;
            }
            byte[] reference = Render(0);
            foreach (int tiling in new[] { 1, 2 })
            {
                if (!Render(tiling).SequenceEqual(reference))
                    throw new Exception($"Cell background seams at dpi={dpi}, origin={origin}, block={block}, tiling={tiling}");
                checks++;
            }
        }
        Console.WriteLine($"PASS {checks} seamless cell background pixel comparisons (100–200% DPI, normal/block spans)");
    }
}
