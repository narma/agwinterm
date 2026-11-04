using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agwinterm.Core;

// One backend per process: native loading, GC and working-set history must not
// contaminate a comparison between engines. No PTY or desktop UI is created.
if (args.Length is < 2 or > 3 || args[0] is not ("managed" or "rust" or "ghostty") ||
    (args[0] != "managed" && args.Length != 3))
{
    Console.Error.WriteLine("Usage: vt-bench managed OUTPUT.json | vt-bench rust OUTPUT.json ABSOLUTE_DLL_PATH | vt-bench ghostty OUTPUT.json DLL_DIRECTORY");
    return 2;
}
string backend = args[0];
string? nativeHash = null;
if (backend == "rust")
{
    string dll = Path.GetFullPath(args[2]);
    if (!RustEmulatorCore.TryLoad(dll, out var error))
        throw new InvalidOperationException(error); // Never benchmark an accidental managed fallback.
    nativeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll)));
}
if (backend == "ghostty")
{
    string directory = Path.GetFullPath(args[2]);
    if (!GhosttyTerminalCore.TryLoad(directory, out var error)) throw new InvalidOperationException(error);
    nativeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "ghostty-vt.dll"))));
}
ITerminalCore Create(int cols, int rows) => backend switch
{
    "rust" => new RustTerminalCore(cols, rows),
    "ghostty" => new GhosttyTerminalCore(cols, rows),
    _ => new TerminalEmulator(cols, rows),
};
var results = new List<object>();
using var process = Process.GetCurrentProcess();
foreach (var (cols, rows) in new[] { (120, 40), (240, 100), (400, 200) })
foreach (int sessions in new[] { 1, 10, 20 })
foreach (string scenario in new[] { "ascii-scroll", "unicode-scroll", "sparse-update" })
foreach (int chunkSize in new[] { 64, 4096 })
{
    byte[] payload = Encoding.UTF8.GetBytes(scenario switch
    {
        "ascii-scroll" => string.Concat(Enumerable.Repeat("build 0123456789 abcdefghijklmnopqrstuvwxyz\r\n", 96)),
        "unicode-scroll" => string.Concat(Enumerable.Repeat("中文 e\u0301 👩🏽‍💻 \u001b[31mred\u001b[0m\r\n", 96)),
        _ => string.Concat(Enumerable.Repeat("\u001b[1;1H\u001b[32mstatus: 12345\u001b[0m", 96)),
    });
    var samples = new List<object>();
    for (int sample = 0; sample < 5; sample++)
    {
        var cores = new List<ITerminalCore>();
        try
        {
            for (int n = 0; n < sessions; n++)
            {
                var core = Create(cols, rows);
                cores.Add(core);
                core.ScrollbackMax = 5000;
            }
            void FeedAll()
            {
                foreach (var core in cores)
                    for (int offset = 0; offset < payload.Length; offset += chunkSize)
                        core.Feed(payload.AsSpan(offset, Math.Min(chunkSize, payload.Length - offset)));
            }
            FeedAll(); // JIT and initialize native/mirror paths before measuring.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var cpuStart = process.TotalProcessorTime;
            long start = Stopwatch.GetTimestamp();
            const int iterations = 4;
            for (int i = 0; i < iterations; i++) FeedAll();
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            double cpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            process.Refresh();
            // Force an observable read outside the timing; do not fold text extraction
            // into parse/adapter throughput or equate it with render performance.
            int checksum = cores.Sum(c => c.CursorRow + c.CursorCol + c.HistoryCount + c.DumpRow(0).Length);
            samples.Add(new { sample, elapsedMs, cpuMs, allocatedBytes,
                inputBytes = (long)payload.Length * iterations * sessions,
                process.PrivateMemorySize64, process.WorkingSet64, checksum });
        }
        finally
        {
            foreach (var core in cores) (core as IDisposable)?.Dispose();
        }
    }
    results.Add(new { cols, rows, sessions, scenario, chunkSize, samples });
    Console.WriteLine($"{backend} {cols}x{rows} sessions={sessions} {scenario} chunk={chunkSize}");
}
var report = new
{
    schemaVersion = 1, backend, nativeHash, nativeAbi = backend == "rust" ? RustEmulatorCore.RequiredAbi : (uint?)null,
    utc = DateTimeOffset.UtcNow, runtime = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    cpuThreads = Environment.ProcessorCount,
    scope = "Headless Feed including adapter work; no renderer, GPU, PTY, input latency or idle measurement. Process memory is an absolute sample, not per-session attribution.",
    results,
};
File.WriteAllText(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return 0;
