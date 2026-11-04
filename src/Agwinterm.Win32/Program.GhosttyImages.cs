using System.Diagnostics;
using Agwinterm.Core;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Agwinterm.Win32;

internal partial class Program
{
    // Native generations are globally unique. Keep these textures separate from
    // host images, whose application-assigned IDs may overlap Kitty IDs.
    private readonly Dictionary<ulong, ID2D1Bitmap> _ghosttyTextures = new();
    private readonly Dictionary<ulong, Task<(byte[] Pixels, int Width, int Height)>> _ghosttyUploads = new();
    private readonly HashSet<ulong> _ghosttyImagesThisFrame = new(), _ghosttyImageFailures = new();
    private readonly Dictionary<GhosttyTerminalCore, System.Threading.Timer> _ghosttyAnimationTimers = new();
    private readonly HashSet<GhosttyTerminalCore> _ghosttyAnimationOwnersThisFrame = new();

    private void ScheduleGhosttyAnimation(GhosttyTerminalCore core)
    {
        _ghosttyAnimationOwnersThisFrame.Add(core);
        if (core.NextAnimationDeadline == 0)
        {
            if (_ghosttyAnimationTimers.Remove(core, out var stopped)) stopped.Dispose();
            return;
        }
        if (!_ghosttyAnimationTimers.TryGetValue(core, out var timer))
        {
            timer = new System.Threading.Timer(_ => RequestRedraw(), null, Timeout.Infinite, Timeout.Infinite);
            _ghosttyAnimationTimers.Add(core, timer);
        }
        timer.Change(Math.Clamp(core.NextAnimationDeadline - Environment.TickCount64, 1, int.MaxValue), Timeout.Infinite);
    }

    private void PrepareGhosttyImages(IReadOnlyList<GhosttyImagePlacement> placements)
    {
        foreach (var placement in placements) _ghosttyImagesThisFrame.Add(placement.Image.Generation);
        foreach (var placement in placements)
        {
            var image = placement.Image; ulong key = image.Generation;
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
            var conversion = Task.Run(() => (image.ToPremultipliedBgra(), image.Width, image.Height));
            _ghosttyUploads.Add(key, conversion);
            // Register after publishing the task, so even a tiny completed image
            // requests a frame that can find its result.
            _ = conversion.ContinueWith(_ => RequestRedraw(), TaskScheduler.Default);
        }
    }

    private void DrawGhosttyImages(IReadOnlyList<GhosttyImagePlacement> placements, GhosttyImageLayer layer,
        float ox, float oy, float cw, float ch, int cols, int rows)
    {
        if (placements.Count == 0) return;
        _rt!.PushAxisAlignedClip(new Vortice.RawRectF(ox, oy, ox + cols * cw, oy + rows * ch), AntialiasMode.Aliased);
        try
        {
            foreach (var p in placements)
            {
                if (p.Layer != layer || !_ghosttyTextures.TryGetValue(p.Image.Generation, out var bitmap)) continue;
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
        foreach (ulong key in _ghosttyTextures.Keys.Where(key => !_ghosttyImagesThisFrame.Contains(key)).ToArray())
        { _ghosttyTextures[key].Dispose(); _ghosttyTextures.Remove(key); }
        foreach (ulong key in _ghosttyUploads.Keys.Where(key => !_ghosttyImagesThisFrame.Contains(key) && _ghosttyUploads[key].IsCompleted).ToArray())
        { _ = _ghosttyUploads[key].Exception; _ghosttyUploads.Remove(key); }
        _ghosttyImageFailures.IntersectWith(_ghosttyImagesThisFrame);
        if (_ghosttyUploads.Count < pending) RequestRedraw(); // freed slots may unblock visible images
    }
}
