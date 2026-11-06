namespace Agwinterm.Core;

/// <summary>Exact selection-local identities for clusters, retaining the scalar ASCII fast path.</summary>
internal sealed class SelectionTextMap
{
    private const uint FirstCluster = 0x110000, Unknown = 0x1fffff;
    private readonly Dictionary<string, uint> _ids = new(StringComparer.Ordinal);
    private readonly List<string> _clusters = new();
    internal uint Encode(Cell cell, bool add = false)
    {
        uint scalar = (uint)cell.Rune;
        if (cell.Grapheme is { } cluster)
        {
            if (!_ids.TryGetValue(cluster, out scalar))
            {
                scalar = Unknown;
                if (add)
                {
                    if (_clusters.Count >= Unknown - FirstCluster) throw new InvalidOperationException("Selection contains too many distinct graphemes.");
                    scalar = FirstCluster + (uint)_clusters.Count;
                    _ids.Add(cluster, scalar); _clusters.Add(cluster);
                }
            }
        }
        return scalar | ((uint)cell.Width << 21);
    }
    internal string? Grapheme(uint scalar) => scalar >= FirstCluster && scalar - FirstCluster < _clusters.Count
        ? _clusters[(int)(scalar - FirstCluster)] : null;
}
