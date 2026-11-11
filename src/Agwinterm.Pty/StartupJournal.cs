using System.Diagnostics;
using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>Bounded, best-effort startup timings. Callers supply only fixed event names and opaque IDs.</summary>
public sealed class StartupJournal : IDisposable
{
    private readonly object _gate = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    private StreamWriter? _writer;
    private int _records;
    public StartupJournal(string directory, string role)
    {
        try
        {
            Directory.CreateDirectory(directory);
            // Separate files avoid contention between GUI and host. Never delete other diagnostics.
            foreach (var old in Directory.GetFiles(directory, $"startup-{role}-*.jsonl")
                         .OrderByDescending(Path.GetFileName).Skip(9))
                try { File.Delete(old); } catch { }
            string path = Path.Combine(directory, $"startup-{role}-{DateTime.UtcNow:yyyyMMddTHHmmssfffffff}-{Environment.ProcessId}.jsonl");
            _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
            Mark("process.enter", typeof(StartupJournal).Assembly.GetName().Version?.ToString());
        }
        catch { /* Diagnostics must never prevent launch. */ }
    }

    public void Mark(string stage, string? id = null, double? durationMs = null)
    {
        lock (_gate)
        {
            if (_writer is null || _records >= 4096) return;
            try
            {
                if (_writer.BaseStream.Position >= 1024 * 1024) return;
                _writer.WriteLine(JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, pid = Environment.ProcessId,
                    elapsedMs = Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
                    stage = stage[..Math.Min(stage.Length, 128)], id = id?[..Math.Min(id.Length, 128)], durationMs }));
                _records++;
            }
            catch { /* A full or unwritable disk must not break the app. */ }
        }
    }

    public IDisposable Measure(string stage, string? id = null) => new Timing(this, stage, id);
    private sealed class Timing : IDisposable
    {
        private readonly StartupJournal _journal;
        private readonly string _stage;
        private readonly string? _id;
        private readonly long _start = Stopwatch.GetTimestamp();
        public Timing(StartupJournal journal, string stage, string? id)
        { _journal = journal; _stage = stage; _id = id; journal.Mark(stage + ".begin", id); }
        public void Dispose() => _journal.Mark(_stage + ".end", _id, Stopwatch.GetElapsedTime(_start).TotalMilliseconds);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }
}
