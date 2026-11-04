namespace Agwinterm.Core;

/// <summary>
/// A position tracked by Ghostty across reflow and history eviction. Access and
/// queries use the terminal's external lock, just like explicit text reads.
/// Disposal is deferred to the next serialized terminal operation.
/// The native allocator owner remains alive until the last anchor is released.
/// </summary>
public sealed class GhosttyAnchor : IDisposable
{
    private readonly GhosttyNative.AnchorHandle _handle;
    internal GhosttyAnchor(GhosttyNative.AnchorHandle handle) => _handle = handle;
    public bool TryLocate(out int row, out int col)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        int result = GhosttyNative.AnchorPoint(_handle, out uint x, out uint y);
        row = col = 0;
        if (result == -4) return false;
        GhosttyNative.Check(result);
        row = checked((int)y); col = checked((int)x); return true;
    }
    public void Dispose() => _handle.Dispose();
}
