using System.Collections;

namespace Agwinterm.Core;

/// <summary>Reservation and presentation are separate: validation/creation must not erase a HUD.
/// Identity checks prevent a late controller from committing over a replacement or removed owner.</summary>
public sealed class WebPageSlots<TOwner, TPage> : IEnumerable<KeyValuePair<TOwner, TPage>>
    where TOwner : notnull where TPage : WebPageState
{
    private readonly object _gate = new();
    private readonly Dictionary<TOwner, TPage> _pages = new();
    public int Count { get { lock (_gate) return _pages.Count; } }
    public TOwner[] Keys { get { lock (_gate) return _pages.Keys.ToArray(); } }
    public bool ContainsKey(TOwner owner) { lock (_gate) return _pages.ContainsKey(owner); }
    public TPage? GetValueOrDefault(TOwner owner) { lock (_gate) return _pages.GetValueOrDefault(owner); }
    public bool TryGetValue(TOwner owner, out TPage page) { lock (_gate) return _pages.TryGetValue(owner, out page!); }
    public bool TryReserve(TOwner owner, TPage page, bool ownerExists, bool programOccupied)
    {
        lock (_gate)
        {
            if (!ownerExists || programOccupied) return false;
            return _pages.TryAdd(owner, page);
        }
    }
    public bool CommitPresentation(TOwner owner, TPage page, Action replaceHud)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_pages.GetValueOrDefault(owner), page)) return false;
            replaceHud();
            return true;
        }
    }
    public bool Remove(TOwner owner, out TPage page) { lock (_gate) return _pages.Remove(owner, out page!); }
    public IEnumerator<KeyValuePair<TOwner, TPage>> GetEnumerator()
    {
        KeyValuePair<TOwner, TPage>[] snapshot;
        lock (_gate) snapshot = _pages.ToArray();
        return ((IEnumerable<KeyValuePair<TOwner, TPage>>)snapshot).GetEnumerator();
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
