using Agwinterm.Win32;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Vortice.WIC;

internal static class RasterChecks
{
    internal static void Run(IDWriteFactory write, IDWriteTextFormat format)
    {
        using var wic = new IWICImagingFactory();
        using var d2d = D2D1.D2D1CreateFactory<ID2D1Factory>();
        using var layouts = new SoloGlyphCache();
        int checks = 0, largestDifference = 0;
        foreach (float dpi in new[] { 96f, 120f, 144f, 192f })
        {
            using var pixels = wic.CreateBitmap(128, 96, PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
            using var target = d2d.CreateWicBitmapRenderTarget(pixels, new RenderTargetProperties
            {
                Type = RenderTargetType.Software,
                PixelFormat = new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                DpiX = dpi, DpiY = dpi,
            });
            target.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
            using var brush = target.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
            using var rasters = new SoloGlyphBitmapCache();
            foreach (string text in new[] { "🔴", "🚀", "👩🏽‍💻", "中", "⏺", "e\u0301" })
            foreach (float x in new[] { 8f, 8.25f, 8.6f })
            foreach (var color in new[] { new Color4(1, 1, 1, 1), new Color4(0.2f, 0.7f, 0.9f, 1) })
            {
                var glyph = layouts.Get(write, text, format, 18, 22);
                brush.Color = color;
                byte[] Render(bool cached)
                {
                    target.BeginDraw();
                    target.Clear(new Color4(0.1f, 0.2f, 0.3f, 1));
                    if (cached) rasters.Draw(target, brush, glyph, x, 8.3f, 18, 22);
                    else SoloGlyphBitmapCache.DrawDirect(target, brush, glyph, x, 8.3f, 18, 22);
                    target.EndDraw().CheckError();
                    var bytes = new byte[128 * 96 * 4]; pixels.CopyPixels(128 * 4, bytes); return bytes;
                }
                byte[] direct = Render(false), first = Render(true), warm = Render(true), reused = Render(true);
                if (!first.SequenceEqual(warm)) throw new Exception("Warm raster changed pixels");
                if (!warm.SequenceEqual(reused)) throw new Exception("Raster reuse changed pixels");
                int max = direct.Zip(warm, (a, b) => Math.Abs(a - b)).Max();
                largestDifference = Math.Max(largestDifference, max);
                // Offscreen premultiplied-alpha composition adds one rounding step.
                if (max > 2) throw new Exception($"Raster mismatch {text} dpi={dpi} x={x}: max channel difference={max}");
                checks++;
            }
            if (rasters.Hits < 36 || rasters.Misses != 36) throw new Exception("Raster key/reuse mismatch");
            // Exercise eviction with real resources and different brush colors.
            var hot = layouts.Get(write, "🔴", format, 18, 22);
            target.BeginDraw();
            for (int i = 0; i < 300; i++)
            {
                brush.Color = new Color4(i / 300f, 0.3f, 0.7f, 1);
                rasters.Draw(target, brush, hot, 8, 8, 18, 22);
                rasters.Draw(target, brush, hot, 8, 8, 18, 22);
            }
            target.EndDraw().CheckError();
            if (rasters.Count != 256 || rasters.Bytes > 16 * 1024 * 1024) throw new Exception("Unbounded raster cache");
            rasters.Clear();
            if (rasters.Count != 0 || rasters.Bytes != 0) throw new Exception("Raster reset retained resources");
            if (dpi == 96)
            {
                target.BeginDraw();
                for (int i = 0; i < 5; i++)
                {
                    brush.Color = new Color4(i / 5f, 0.2f, 0.5f, 1);
                    rasters.Draw(target, brush, hot, 8, 8, 1024, 1024);
                    rasters.Draw(target, brush, hot, 8, 8, 1024, 1024);
                }
                target.EndDraw().CheckError();
                if (rasters.Count != 4 || rasters.Bytes != 16 * 1024 * 1024) throw new Exception("Raster byte budget eviction failed");
                rasters.Clear();
                // A single pass over unique keys must not allocate native bitmaps.
                target.BeginDraw();
                for (int i = 0; i < 300; i++)
                {
                    brush.Color = new Color4(i / 300f, 0.1f, 0.6f, 1);
                    rasters.Draw(target, brush, hot, 8, 8, 18, 22);
                }
                target.EndDraw().CheckError();
                if (rasters.Count != 0 || rasters.Misses != 0) throw new Exception("One-off glyphs allocated raster surfaces");
            }
        }
        Console.WriteLine($"PASS {checks} offscreen raster comparisons, eviction/reset; max channel difference={largestDifference}");
    }
}
