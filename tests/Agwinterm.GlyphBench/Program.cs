using System.Diagnostics;
using System.Text.Json;
using Agwinterm.Win32;
using Vortice.DirectWrite;

// Host-free: DirectWrite layouts and offscreen WIC targets only; no windows or input.
using var factory = DWrite.DWriteCreateFactory<IDWriteFactory>();
using var format = factory.CreateTextFormat("Consolas", 14f);
CellBackgroundChecks.Run();
if (args.Contains("--cell-background-only")) return;
RasterChecks.Run(factory, format);
if (args.Contains("--raster-only")) return;
using var bold = factory.CreateTextFormat("Consolas", null, FontWeight.Bold, FontStyle.Normal, FontStretch.Normal, 14f, "en-us");
using var cache = new SoloGlyphCache();
var a = cache.Get(factory, 0x1f534, format, 18, 18);
Assert(ReferenceEquals(a, cache.Get(factory, 0x1f534, format, 18, 18)), "layout reuse");
Assert(!ReferenceEquals(a, cache.Get(factory, 0x1f534, format, 9, 18)), "clip width in key");
Assert(!ReferenceEquals(a, cache.Get(factory, 0x1f534, format, 18, 22)), "height in key");
Assert(!ReferenceEquals(a, cache.Get(factory, 0x1f534, bold, 18, 18)), "style in key");
// Keep a hot emoji while scanning beyond capacity; the least recently used layout must be released.
var cold = cache.Get(factory, 0x4e00, format, 18, 18);
for (int i = 1; i <= 300; i++)
{
    cache.Get(factory, 0x4e00 + i, format, 18, 18);
    cache.Get(factory, 0x1f534, format, 18, 18);
}
Assert(cache.Count == 256, "bounded capacity");
Assert(cold.Layout.NativePointer == IntPtr.Zero, "evicted native layout released");
Assert(a.Layout.NativePointer != IntPtr.Zero, "hot layout survives LRU eviction");
cache.Clear();
Assert(a.Layout.NativePointer == IntPtr.Zero && cache.Count == 0, "font-reset disposal");
Assert(cache.Get(factory, 0x1f534, format, 18, 18).Layout.NativePointer != IntPtr.Zero, "reuse after font reset");
cache.Clear();
Console.WriteLine("PASS 9 cache lifetime/key checks");
var cluster = cache.Get(factory, "👩🏽‍💻", format, 18, 18);
Assert(ReferenceEquals(cluster, cache.Get(factory, "👩🏽‍💻", format, 18, 18)), "cluster layout reuse");
Assert(!ReferenceEquals(cluster, cache.Get(factory, "👩💻", format, 18, 18)), "complete cluster in cache key");
cache.Clear();
Assert(cluster.Layout.NativePointer == IntPtr.Zero, "cluster layout disposal");
Console.WriteLine("PASS 3 cluster cache checks");

// Include the separate surrogate cells produced by legacy text-only SeedScrollback.
foreach (int rune in new[] { 0x1f534, 0x4e2d, 0x23fa, 0x2714, 0xd83d, 0xdd34 })
    foreach (var face in new[] { format, bold })
        foreach (float width in new[] { 9f, 18f })
            foreach (float height in new[] { 18f, 22f })
            {
                var actual = cache.Get(factory, rune, face, width, height);
                if (!actual.HasInk) continue;
                var ink = actual.Layout.OverhangMetrics;
                Assert(actual.Scale > 0 && actual.Scale <= 1, "ink is never enlarged");
                Assert(actual.Dx - ink.Left * actual.Scale >= -0.001f &&
                    actual.Dy - ink.Top * actual.Scale >= -0.001f &&
                    actual.Dx + (width + ink.Right) * actual.Scale <= width + 0.001f &&
                    actual.Dy + (height + ink.Bottom) * actual.Scale <= height + 0.001f, "ink fits its cell rectangle");
            }
cache.Clear();
Console.WriteLine("PASS 48 ink containment checks (including legacy surrogate cells)");

int[] icons = [0x1f534, 0x4e2d, 0x1f535, 0x754c, 0x23fa, 0x2714];
var results = new List<object>();
foreach (var scenario in new[] { "sparse-6", "cold-6", "dense-2000", "churn-512" })
{
    int count = scenario.EndsWith("-6") ? 6 : scenario == "dense-2000" ? 2000 : 512;
    int frames = scenario.EndsWith("-6") ? 1000 : 10;
    int Rune(int i) => scenario == "churn-512" ? 0x4e00 + i : icons[i % icons.Length];
    void Frame(bool cached)
    {
        if (cached && scenario == "cold-6") cache.Clear();
        for (int i = 0; i < count; i++)
        {
            if (cached) BenchmarkSink.Value += cache.Get(factory, Rune(i), format, 18, 18).Scale;
            else BenchmarkSink.Value += OriginalPrepare(factory, Rune(i), format, 18, 18).Scale;
        }
    }
    for (int i = 0; i < 30; i++) { Frame(false); Frame(true); }
    var timings = new[] { new List<double>(), new List<double>() };
    var allocations = new[] { new List<double>(), new List<double>() };
    // Alternate order to reduce warmup/thermal/order bias; timings are ms per synthetic frame.
    for (int round = 0; round < 12; round++)
        for (int order = 0; order < 2; order++)
        {
            int mode = (round + order) % 2;
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            for (int frame = 0; frame < frames; frame++) Frame(mode == 1);
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
            timings[mode].Add(elapsed / frames);
            allocations[mode].Add(allocated / (double)frames);
        }
    for (int mode = 0; mode < 2; mode++)
    {
        var values = timings[mode].Order().ToArray();
        results.Add(new { scenario, mode = mode == 0 ? "uncached" : "cached", glyphsPerFrame = count,
            medianMs = (values[5] + values[6]) / 2, minMs = values[0], maxMs = values[^1],
            managedBytesPerFrame = allocations[mode].Average(), samplesMs = timings[mode] });
    }
    cache.Clear();
}
string json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);
if (args.Length == 1) File.WriteAllText(args[0], json);

// Preparation copied from PR 675b9db's DrawSoloGlyph, excluding draw/transform/clip calls.
// Return the fit so the compiler must preserve its arithmetic; neither path renders pixels.
// This timing baseline retains the old padding; pixel acceptance lives in the VM suite.
static (float Scale, float Dx, float Dy) OriginalPrepare(IDWriteFactory factory, int rune,
    IDWriteTextFormat format, float width, float height)
{
    string text = rune > 0xffff ? char.ConvertFromUtf32(rune) : ((char)rune).ToString();
    using var layout = factory.CreateTextLayout(text, format, width, height);
    var overhang = layout.OverhangMetrics;
    float left = -overhang.Left, top = -overhang.Top;
    float right = width + overhang.Right, bottom = height + overhang.Bottom;
    float inkWidth = right - left, inkHeight = bottom - top;
    if (inkWidth <= 0 || inkHeight <= 0) return default;
    float padding = MathF.Min(0.5f, MathF.Min(width, height) * 0.1f);
    float scale = MathF.Min(1f, MathF.Min((width - 2 * padding) / inkWidth, (height - 2 * padding) / inkHeight));
    float minX = padding - left * scale, minY = padding - top * scale;
    float dx = Math.Clamp(0f, minX, MathF.Max(minX, width - padding - right * scale));
    float dy = Math.Clamp(0f, minY, MathF.Max(minY, height - padding - bottom * scale));
    return (scale, dx, dy);
}

static void Assert(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException(message);
}

static class BenchmarkSink { internal static float Value; }
