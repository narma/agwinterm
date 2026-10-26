using System.Diagnostics;
using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>Ordered background mutations, merged with fresh disk state under a cross-process lock.</summary>
public sealed class RecentHistoryWriter
{
    private sealed record Change(RecentHistory Before, RecentHistory After, bool ClearAll);
    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<Change> _pending = [];
    private Task<RecentHistory> _tail = Task.FromResult(new RecentHistory());
    private string? _warning;
    public string? Warning => Volatile.Read(ref _warning);
    public RecentHistoryWriter(string path) => _path = Path.GetFullPath(path);

    public Task<RecentHistory> Enqueue(RecentHistory before, RecentHistory after, bool clearAll = false)
    {
        // Freeze the UI-owned state before handing it to the worker.
        var change = new Change(before.Copy(), after.Copy(), clearAll);
        return Queue(() => { _pending.Add(change); return ApplyPending(); });
    }

    public Task<RecentHistory> ReadAsync() => Queue(ApplyPending);

    private Task<RecentHistory> Queue(Func<RecentHistory> work)
    {
        lock (_gate)
            return _tail = _tail.ContinueWith(_ =>
            {
                try { return work(); }
                catch (Exception e)
                {
                    Volatile.Write(ref _warning, "Recent history not saved: " + e.Message);
                    throw;
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private FileStream LockFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(10)) { Thread.Sleep(25); }
        }
    }

    private RecentHistory ApplyPending()
    {
        using var locked = LockFile();
        var current = RecentHistory.Load(_path, throwOnIo: true);
        foreach (var change in _pending) Apply(current, change);
        if (_pending.Count > 0)
        {
            if (!current.Save(_path)) throw new IOException(current.Warning);
            _pending.Clear(); // Failed writes keep their operations for the next attempt.
        }
        Volatile.Write(ref _warning, current.Warning);
        return current;
    }

    private static void Apply(RecentHistory current, Change change)
    {
        if (change.ClearAll) { current.Sessions.Clear(); current.Workspaces.Clear(); }
        Merge(current.Sessions, change.Before.Sessions, change.After.Sessions, s => s.EntryId,
            s => s with { Seq = current.NextSequence() }, (old, next) => next with { Seq = old.Seq });
        Merge(current.Workspaces, change.Before.Workspaces, change.After.Workspaces, w => w.EntryId,
            w => w with { Seq = current.NextSequence() }, (old, next) => next with
            {
                Seq = old.Seq,
                Sessions = next.Sessions.Where(s => old.Sessions.Any(existing => existing.EntryId == s.EntryId)).ToArray(),
                DestinationWindowId = old.DestinationWindowId ?? next.DestinationWindowId,
                DestinationWorkspaceId = old.DestinationWorkspaceId ?? next.DestinationWorkspaceId
            });
    }

    private static void Merge<T>(List<T> current, List<T> before, List<T> after, Func<T, string> id, Func<T, T> stamp, Func<T, T, T> update)
    {
        var old = before.ToDictionary(id, StringComparer.Ordinal);
        var next = after.ToDictionary(id, StringComparer.Ordinal);
        current.RemoveAll(item => old.ContainsKey(id(item)) && !next.ContainsKey(id(item)));
        foreach (var item in after)
        {
            string key = id(item);
            int index = current.FindIndex(existing => id(existing) == key);
            if (!old.TryGetValue(key, out var previous))
            {
                if (index < 0) current.Add(stamp(item));
            }
            else if (index >= 0 && JsonSerializer.Serialize(previous) != JsonSerializer.Serialize(item))
                current[index] = update(current[index], item);
            // An unchanged old entry is never reinserted after another process clears it.
        }
    }
}
