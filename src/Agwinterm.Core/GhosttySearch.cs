namespace Agwinterm.Core;

/// <summary>A persistent native search: feed under terminal ownership, tick on
/// copied pages outside it. Reuses upstream progress across output and reflow.</summary>
public sealed class GhosttySearch : IDisposable
{
    public const int MatchLimit = 100_000;
    private readonly GhosttyTerminalCore _core;
    private readonly GhosttyVt _vt;
    private readonly string _query;
    private readonly object _lifetime = new();
    private readonly SemaphoreSlim _runGate = new(1);
    private GhosttyNative.SearchHandle? _handle;
    private bool _disposed, _limited;
    private long _restoreVersion = -1, _version = -1;
    private uint _status = 1;
    internal GhosttySearch(GhosttyTerminalCore core, GhosttyVt vt, string query)
    {
        _core = core; _vt = vt; _query = query;
    }

    public Task<GhosttyTerminalCore.SearchResult> RunAsync(object syncRoot, CancellationToken cancellation)
        => Task.Run(() => Run(syncRoot, cancellation), cancellation);

    internal unsafe GhosttyTerminalCore.SearchResult Run(object syncRoot, CancellationToken cancellation)
    {
        _runGate.Wait(cancellation);
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                GhosttyNative.SearchHandle handle;
                uint[]? coordinates = null; long version = 0; bool truncated = false;
                lock (syncRoot)
                {
                    _core.CheckSearchAlive();
                    lock (_lifetime)
                    {
                        ObjectDisposedException.ThrowIf(_disposed, this);
                        if (_handle is null || _restoreVersion != _vt.RestoreVersion)
                        {
                            _handle?.Dispose(); _handle = _vt.CreateSearch(_query);
                            _restoreVersion = _vt.RestoreVersion; _version = -1; _status = 1; _limited = false;
                        }
                        handle = _handle;
                    }
                    if (_status == 1 || _version != _core.TextVersion)
                    {
                        GhosttyNative.Check(GhosttyNative.SearchFeed(handle, out _status, out nuint currentCount));
                        _version = _core.TextVersion;
                        _limited = currentCount > MatchLimit;
                    }
                    if (_status == 2 || _limited)
                    {
                        GhosttyNative.Check(GhosttyNative.SearchMatches(handle, null, 0, out nuint count));
                        _limited = count > MatchLimit;
                        // Export prunes evicted references. If that drops us below
                        // the cap, finish scanning before publishing a full result.
                        if (_status == 2 || _limited)
                        {
                            truncated = _limited;
                            int take = (int)Math.Min(count, (nuint)MatchLimit);
                            coordinates = new uint[take * 4];
                            fixed (uint* output = coordinates)
                                GhosttyNative.Check(GhosttyNative.SearchMatches(handle, output, (nuint)take, out count));
                            version = _version;
                        }
                    }
                }
                if (coordinates is not null)
                {
                    var matches = new (int Line, int Col0, int EndLine, int Col1)[coordinates.Length / 4];
                    for (int i = 0; i < matches.Length; i++)
                        matches[i] = ((int)coordinates[i*4], (int)coordinates[i*4+1], (int)coordinates[i*4+2], (int)coordinates[i*4+3]);
                    Array.Sort(matches);
                    // Keep the UI list unique defensively; folded-offset
                    // duplicates are already removed inside native search.
                    int unique = 0;
                    foreach (var match in matches)
                        if (unique == 0 || matches[unique - 1] != match) matches[unique++] = match;
                    if (unique != matches.Length) Array.Resize(ref matches, unique);
                    return new(version, matches, truncated);
                }
                GhosttyNative.Check(GhosttyNative.SearchTick(handle, out _status, out nuint found));
                _limited = found > MatchLimit;
                Thread.Yield();
            }
        }
        finally { _runGate.Release(); }
    }

    public void Dispose()
    {
        lock (_lifetime) { _disposed = true; _handle?.Dispose(); }
    }
}
