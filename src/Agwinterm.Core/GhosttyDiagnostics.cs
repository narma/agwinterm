namespace Agwinterm.Core;

internal enum GhosttyInstanceRole { Owner, LegacyReplica }

/// <summary>
/// Process-local resource counts, enabled before startup with AGWINTERM_GHOSTTY_COUNTERS=1.
/// Counts native terminal handles (including retained handles), not native heap bytes.
/// Sum host and UI snapshots when inspecting hosted sessions. No terminal text is recorded.
/// </summary>
public static class GhosttyDiagnostics
{
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("AGWINTERM_GHOSTTY_COUNTERS") == "1";
    private static readonly object Gate = new();
    private static long _owners, _replicas, _allocated, _released, _feeds, _bytes, _captures, _searches;
    private static long _ownerFeedBytes, _replicaFeedBytes;
    public sealed record NativeCounters(ulong CurrentBytes, ulong PeakBytes, ulong AllocatedBytes, ulong Allocations, ulong Frees);
    public static NativeCounters? NativeAllocationSnapshot() => Enabled ? GhosttyNative.AllocationStats() : null;
    public sealed record NativePages(ulong ResidentBytes,ulong VirtualBytes,ulong ImageBytes);
    public static NativePages? NativePageSnapshot() => Enabled ? GhosttyNative.PageStats() : null;
    public sealed record Counters(long Owners, long LegacyReplicas, long TerminalsAllocated,
        long TerminalsReleased, long FeedCalls, long FeedBytes, long ActiveCaptures, long SearchHandles,
        long OwnerFeedBytes, long LegacyReplicaFeedBytes);
    public static Counters Snapshot()
    {
        lock (Gate) return new(_owners, _replicas, _allocated, _released, _feeds, _bytes, _captures, _searches,
            _ownerFeedBytes, _replicaFeedBytes);
    }
    internal static void Allocate()
    {
        if (!Enabled) return;
        lock (Gate) { _owners++; _allocated++; }
    }
    internal static void Release(GhosttyInstanceRole role)
    {
        if (!Enabled) return;
        lock (Gate) { if (role == GhosttyInstanceRole.Owner) _owners--; else _replicas--; _released++; }
    }
    internal static void MarkReplica()
    {
        if (!Enabled) return;
        lock (Gate) { _owners--; _replicas++; }
    }
    internal static void Feed(int bytes, GhosttyInstanceRole role)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            _feeds++; _bytes += bytes;
            if (role == GhosttyInstanceRole.Owner) _ownerFeedBytes += bytes; else _replicaFeedBytes += bytes;
        }
    }
    internal static void Capture(int delta)
    {
        if (!Enabled) return;
        lock (Gate) _captures += delta;
    }
    internal static void Search(int delta)
    {
        if (!Enabled) return;
        lock (Gate) _searches += delta;
    }
}
