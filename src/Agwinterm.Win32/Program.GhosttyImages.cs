using System.Diagnostics;
using Agwinterm.Core;
using Agwinterm.Pty;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Agwinterm.Win32;

internal partial class Program
{
    // Generations are unique inside one owner process, not across hosts or a
    // host restart. Scope GPU resources to the UI session and image generation.
    private readonly Dictionary<(ISession Session, ulong Generation), ID2D1Bitmap> _ghosttyTextures = new();
    private readonly Dictionary<(ISession Session, ulong Generation), Task<(byte[] Pixels, int Width, int Height)>> _ghosttyUploads = new();
    private readonly HashSet<(ISession Session, ulong Generation)> _ghosttyImagesThisFrame = new(), _ghosttyImageFailures = new();
    private readonly Dictionary<ISession, System.Threading.Timer> _ghosttyAnimationTimers = new();
    private readonly HashSet<ISession> _ghosttyAnimationOwnersThisFrame = new();

    private void ScheduleGhosttyAnimation(ISession core, long deadline)
    {
        _ghosttyAnimationOwnersThisFrame.Add(core);
        if (deadline == 0)
        {
            if (_ghosttyAnimationTimers.Remove(core, out var stopped)) stopped.Dispose();
            return;
        }
        if (!_ghosttyAnimationTimers.TryGetValue(core, out var timer))
        {
            timer = new System.Threading.Timer(_ => RequestRedraw(), null, Timeout.Infinite, Timeout.Infinite);
            _ghosttyAnimationTimers.Add(core, timer);
        }
        timer.Change(Math.Clamp(deadline - Environment.TickCount64, 1, int.MaxValue), Timeout.Infinite);
    }

    private void PrepareGhosttyImages(ISession session, IReadOnlyList<GhosttyImagePlacement> placements)
    {
        foreach (var placement in placements) _ghosttyImagesThisFrame.Add((session,placement.Image.Generation));
        foreach (var placement in placements)
        {
            var image = placement.Image; var key = (session,image.Generation);
            if (_ghosttyTextures.ContainsKey(key) || _ghosttyImageFailures.Contains(key)) continue;
            if (_ghosttyUploads.TryGetValue(key, out var task))
            {
                if (!task.IsCompleted) continue;
                _ghosttyUploads.Remove(key);
                if (task.IsCompletedSuccessfully)
                {
                    var decoded = task.Result;
                    long start = Stopwatch.GetTimestamp();
                    unsafe
                    {
                        fixed (byte* pixels = decoded.Pixels)
                            _ghosttyTextures[key] = _rt!.CreateBitmap(new SizeI(decoded.Width, decoded.Height),
                                (nint)pixels, checked((uint)decoded.Width * 4), _bmpProps);
                    }
                    _uploadCount++; _uploadMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                }
                else { _ = task.Exception; _ghosttyImageFailures.Add(key); }
                continue;
            }
            if (_ghosttyUploads.Count >= MaxConcurrentImageDecodes) continue;
            var conversion = Task.Run(() => image.Format == GhosttyPixelFormat.Png
                ? DecodePixels(new KittyImage(0,KittyFormat.Png,image.Width,image.Height,image.Pixels.ToArray()))
                : (image.ToPremultipliedBgra(), image.Width, image.Height));
            _ghosttyUploads.Add(key, conversion);
            // Register after publishing the task, so even a tiny completed image
            // requests a frame that can find its result.
            _ = conversion.ContinueWith(_ => RequestRedraw(), TaskScheduler.Default);
        }
    }

    private void DrawGhosttyImages(ISession session, IReadOnlyList<GhosttyImagePlacement> placements, GhosttyImageLayer layer,
        float ox, float oy, float cw, float ch, int cols, int rows)
    {
        if (placements.Count == 0) return;
        _rt!.PushAxisAlignedClip(new Vortice.RawRectF(ox, oy, ox + cols * cw, oy + rows * ch), AntialiasMode.Aliased);
        try
        {
            foreach (var p in placements)
            {
                if (p.Layer != layer || !_ghosttyTextures.TryGetValue((session,p.Image.Generation), out var bitmap)) continue;
                float x = ox + p.Col * cw + p.OffsetX, y = oy + p.Row * ch + p.OffsetY;
                _rt.DrawBitmap(bitmap, new Vortice.RawRectF(x, y, x + p.Width, y + p.Height), 1,
                    BitmapInterpolationMode.Linear, new Vortice.RawRectF(p.SourceX, p.SourceY,
                        (float)p.SourceX + p.SourceWidth, (float)p.SourceY + p.SourceHeight));
            }
        }
        finally { _rt.PopAxisAlignedClip(); }
    }

    private void PruneGhosttyImages()
    {
        foreach (var core in _ghosttyAnimationTimers.Keys.Where(core => !_ghosttyAnimationOwnersThisFrame.Contains(core)).ToArray())
        { _ghosttyAnimationTimers[core].Dispose(); _ghosttyAnimationTimers.Remove(core); }
        int pending = _ghosttyUploads.Count;
        foreach (var key in _ghosttyTextures.Keys.Where(key => !_ghosttyImagesThisFrame.Contains(key)).ToArray())
        { _ghosttyTextures[key].Dispose(); _ghosttyTextures.Remove(key); }
        foreach (var key in _ghosttyUploads.Keys.Where(key => !_ghosttyImagesThisFrame.Contains(key) && _ghosttyUploads[key].IsCompleted).ToArray())
        { _ = _ghosttyUploads[key].Exception; _ghosttyUploads.Remove(key); }
        _ghosttyImageFailures.IntersectWith(_ghosttyImagesThisFrame);
        if (_ghosttyUploads.Count < pending) RequestRedraw(); // freed slots may unblock visible images
    }
    private void DisposeGhosttyRendering()
    {
        foreach (var timer in _ghosttyAnimationTimers.Values) timer.Dispose();
        _ghosttyAnimationTimers.Clear();
        foreach (var bitmap in _ghosttyTextures.Values) bitmap.Dispose();
        _ghosttyTextures.Clear();
    }
}
