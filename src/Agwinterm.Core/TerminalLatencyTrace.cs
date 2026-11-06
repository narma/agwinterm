using System.Diagnostics;
using System.Threading.Channels;

namespace Agwinterm.Core;

/// <summary>Opt-in fixture diagnostics. Never writes terminal contents to the trace.
/// T8 is application presentation submission, not physical display time.</summary>
public static class TerminalLatencyTrace
{
    private static readonly string? DirectoryPath = Environment.GetEnvironmentVariable("AGWINTERM_LATENCY_TRACE");
    public static bool Enabled => !string.IsNullOrWhiteSpace(DirectoryPath);
    private readonly record struct Entry(int Stage, long Id, long Timestamp, long Frame);
    private static readonly Channel<Entry>? Queue = Enabled ? Channel.CreateBounded<Entry>(131072) : null;
    private static long _lost, _inputSequence;
    private static readonly long[] MetricCounts = new long[64], MetricTotals = new long[64], MetricMaxima = new long[64];
    private static readonly long[] MetricBuckets = new long[64*64];
    static TerminalLatencyTrace()
    {
        if (Enabled) { _ = Task.Run(WriteLoop); _ = Task.Run(MetricsLoop); }
    }
    public static void Record(int stage, long id, long timestamp = 0, long frame = 0)
    {
        if (Queue is null || id <= 0) return;
        if (stage is >= 20 and < 64)
        {
            // Feed/lock/IPC observations can exceed millions per run. Aggregate them
            // without per-event queueing, formatting, disk IO or terminal contents.
            Interlocked.Increment(ref MetricCounts[stage]); Interlocked.Add(ref MetricTotals[stage],id);
            Interlocked.Increment(ref MetricBuckets[stage*64+System.Numerics.BitOperations.Log2((ulong)id)]);
            long maximum=Volatile.Read(ref MetricMaxima[stage]);
            while(id > maximum)
            {
                long observed=Interlocked.CompareExchange(ref MetricMaxima[stage],id,maximum);
                if(observed == maximum) break;
                maximum=observed;
            }
            return;
        }
        if (!Queue.Writer.TryWrite(new(stage, id, timestamp == 0 ? Stopwatch.GetTimestamp() : timestamp, frame)))
            Interlocked.Increment(ref _lost);
    }
    public static void EnterReceived() { if (Enabled) Record(1, Interlocked.Increment(ref _inputSequence)); }
    public readonly struct LockTiming : IDisposable
    {
        private readonly long _acquired;
        private readonly int _stage;
        public LockTiming(long waitingSince, int stage)
        {
            _stage = stage; _acquired = Enabled ? Stopwatch.GetTimestamp() : 0;
            if (_acquired != 0) Record(stage, Math.Max(1, _acquired - waitingSince), _acquired);
        }
        public void Dispose()
        {
            if (_acquired != 0) Record(_stage + 1, Math.Max(1, Stopwatch.GetTimestamp() - _acquired));
        }
    }
    private static async Task MetricsLoop()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath!);
            using var process = Process.GetCurrentProcess();
            using var file = new FileStream(Path.Combine(DirectoryPath!, $"metrics-{Environment.ProcessId}.csv"),
                FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(file) { AutoFlush = true };
            using var timingFile = new FileStream(Path.Combine(DirectoryPath!, $"timings-{Environment.ProcessId}.csv"),
                FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            using var timingWriter = new StreamWriter(timingFile) { AutoFlush = true };
            await timingWriter.WriteLineAsync("timestamp,stage,count,total,maximum,"+string.Join(',',Enumerable.Range(0,64).Select(i=>"log2_"+i)));
            await writer.WriteLineAsync("timestamp,cpuTicks,privateBytes,workingSet,managedAllocated,gen0,gen1,gen2,gcPauseTicks,nativeCurrent,nativePeak,nativeAllocated,nativeAllocations,nativeFrees,owners,replicas,ownerFeedBytes,replicaFeedBytes,nativePageResident,nativePageVirtual,nativeImageBytes,gcHeapSizeBytes,gcCommittedBytes,gcFragmentedBytes,managedLiveBytes");
            while (true)
            {
                process.Refresh();
                var native = GhosttyDiagnostics.NativeAllocationSnapshot();
                var pages = GhosttyDiagnostics.NativePageSnapshot();
                var counts = GhosttyDiagnostics.Snapshot();
                var heap = GC.GetGCMemoryInfo();
                await writer.WriteLineAsync($"{Stopwatch.GetTimestamp()},{process.TotalProcessorTime.Ticks},{process.PrivateMemorySize64},{process.WorkingSet64},{GC.GetTotalAllocatedBytes()},{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)},{GC.GetTotalPauseDuration().Ticks},{native?.CurrentBytes},{native?.PeakBytes},{native?.AllocatedBytes},{native?.Allocations},{native?.Frees},{counts.Owners},{counts.LegacyReplicas},{counts.OwnerFeedBytes},{counts.LegacyReplicaFeedBytes},{pages?.ResidentBytes},{pages?.VirtualBytes},{pages?.ImageBytes},{heap.HeapSizeBytes},{heap.TotalCommittedBytes},{heap.FragmentedBytes},{GC.GetTotalMemory(false)}");
                for(int stage=20;stage<64;stage++)
                {
                    long count=Volatile.Read(ref MetricCounts[stage]);
                    if(count == 0) continue;
                    var buckets=new long[64];
                    for(int i=0;i<64;i++) buckets[i]=Volatile.Read(ref MetricBuckets[stage*64+i]);
                    await timingWriter.WriteLineAsync($"{Stopwatch.GetTimestamp()},{stage},{count},{Volatile.Read(ref MetricTotals[stage])},{Volatile.Read(ref MetricMaxima[stage])},"+string.Join(',',buckets));
                }
                await Task.Delay(250);
            }
        }
        catch { Interlocked.Increment(ref _lost); }
    }
    private static async Task WriteLoop()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath!);
            using var file = new FileStream(Path.Combine(DirectoryPath!, $"trace-{Environment.ProcessId}.csv"),
                FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(file);
            await writer.WriteLineAsync($"frequency,{Stopwatch.Frequency},pid,{Environment.ProcessId}");
            await writer.FlushAsync();
            while (await Queue!.Reader.WaitToReadAsync())
            {
                while (Queue.Reader.TryRead(out var entry))
                    await writer.WriteLineAsync($"{entry.Stage},{entry.Id},{entry.Timestamp},{entry.Frame}");
                await writer.WriteLineAsync($"lost,{Interlocked.Read(ref _lost)}");
                await writer.FlushAsync();
            }
        }
        catch { Interlocked.Increment(ref _lost); } // Missing/truncated evidence invalidates the run.
    }

    /// <summary>Bounded streaming recognizer for the fixture's ASCII marker, including split reads.</summary>
    public sealed class MarkerReader
    {
        private readonly bool _countEnters;
        private long _enterCount;
        public MarkerReader() : this(Environment.GetEnvironmentVariable("AGWINTERM_LATENCY_INPUT") == "single-key") { }
        public MarkerReader(bool countEnters) => _countEnters=countEnters;
        private int _position;
        private long _id, _pending;
        private readonly char[] _encoded = new char[128];
        private int _encodedLength, _escape;
        private long InputByte(byte value)
        {
            if (_escape == 0)
            {
                if (value == 27) { _escape = 1; return 0; }
                return Push(value, true);
            }
            if (_escape == 1)
            {
                _escape = value == '[' ? 2 : 0; _encodedLength = 0; return 0;
            }
            if (value is >= 0x40 and <= 0x7e)
            {
                _escape = 0;
                var fields = new string(_encoded, 0, _encodedLength).Split(';');
                if (value == '_' && fields.Length == 6 && fields[3] == "1" &&
                    int.TryParse(fields[2], out int character) && character is > 0 and <= 127)
                    return Push((byte)character, true);
                if (value == 'u' && fields.Length >= 1 && fields[0] == "13") return Push(13, true);
                return 0;
            }
            if (_encodedLength == _encoded.Length) { _escape = 0; return 0; }
            _encoded[_encodedLength++] = (char)value; return 0;
        }
        public long Push(byte value, bool requireEnter = false)
        {
            if (requireEnter && value == 13) { long result = _countEnters ? ++_enterCount : _pending; _pending = 0; _position = 0; return result; }
            const string prefix = "~AGL";
            if (_position < prefix.Length)
            {
                if (value == prefix[_position]) _position++;
                else _position = value == '~' ? 1 : 0;
                if (_position == 4) _id = 0;
                return 0;
            }
            if (_position < 12)
            {
                int digit = value is >= (byte)'0' and <= (byte)'9' ? value - '0' :
                    value is >= (byte)'A' and <= (byte)'F' ? value - 'A' + 10 : -1;
                if (digit < 0) { _position = value == '~' ? 1 : 0; return 0; }
                _id = (_id << 4) | (uint)digit; _position++; return 0;
            }
            _position = value == '~' ? 1 : 0;
            if (value != '~' || _id is <= 0 or > 100000) return 0;
            if (requireEnter) { _pending = _id; return 0; }
            return _id;
        }
        public long Input(ReadOnlySpan<byte> bytes)
        {
            long result = 0;
            foreach (byte value in bytes)
            {
                long id = InputByte(value);
                if (id > 0) { if (result > 0) throw new InvalidDataException("Echo fixture requires one event per write."); result = id; }
            }
            return result;
        }
        public void Output(ReadOnlySpan<byte> bytes, int stage = 5)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                if (_position == 0)
                {
                    int next = bytes[offset..].IndexOf((byte)'~');
                    if (next < 0) return;
                    offset += next;
                }
                long id = Push(bytes[offset++]);
                if (id > 0) Record(stage, id);
            }
        }
    }
    /// <summary>The fixture reserves row 1, columns 0..12. Inspect actual captured cells,
    /// without allocating text for the entire viewport on every instrumented paint.</summary>
    public static long FrameMarker(ReadOnlySpan<Cell> row)
    {
        if (row.Length < 13) return 0;
        long id = 0;
        for (int i = 0; i < 13; i++)
        {
            var cell = row[i];
            if (cell.Width != 1 || cell.Grapheme is { Length: > 1 }) return 0;
            int value = cell.Grapheme is { Length: 1 } text ? text[0] : cell.Rune;
            if (i < 4) { if (value != "~AGL"[i]) return 0; }
            else if (i == 12) { if (value != '~') return 0; }
            else
            {
                int digit = value is >= '0' and <= '9' ? value - '0' : value is >= 'A' and <= 'F' ? value - 'A' + 10 : -1;
                if (digit < 0) return 0;
                id = (id << 4) | (uint)digit;
            }
        }
        return id is > 0 and <= 100000 ? id : 0;
    }
}
