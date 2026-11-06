using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agwinterm.Core;

if (args.Length != 3 || args[0] != "ghostty")
{
    Console.Error.WriteLine("Usage: vt-bench ghostty OUTPUT.json DLL_DIRECTORY");
    return 2;
}
const string backend = "ghostty";
string directory = Path.GetFullPath(args[2]);
if (!GhosttyTerminalCore.TryLoad(directory, out var error)) throw new InvalidOperationException(error);
string nativeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "ghostty-vt.dll"))));
using var provenance = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "ghostty-vt.provenance.json")));
int nativeAbi = provenance.RootElement.GetProperty("abi").GetInt32();
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
                var core = new GhosttyTerminalCore(cols, rows);
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
    schemaVersion = 1, backend, nativeHash, nativeAbi,
    utc = DateTimeOffset.UtcNow, runtime = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    cpuThreads = Environment.ProcessorCount,
    scope = "Headless Feed including adapter work; no renderer, GPU, PTY, input latency or idle measurement. Process memory is an absolute sample, not per-session attribution.",
    results,
};
File.WriteAllText(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return 0;
