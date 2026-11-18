using Agwinterm.Pty;

namespace Agwinterm.Win32;

internal partial class Program
{
    private static StartupJournal? _startupJournal;
    private string? _startupProgress;
    private IReadOnlySet<string>? _restoreHostedIds;
    private Task<IReadOnlySet<string>?>? _restoreInventory;
    private const int RestoreTimer = 16;
    private IEnumerator<bool>? _restoreSteps;
    private volatile TaskCompletionSource<bool>? _restoreDone;
    private bool _restoreTimerArmed;
    private long _lastRestoreSlice;
    private bool _firstKeyLogged;

    private void ContinueRestore(bool finish = false, [System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        if (_restoreSteps is not { } steps) return;
        if (!finish && _restoreInventory is { IsCompleted: false }) return;
        // Let the host echo a recent key before competing for its control pipe
        // with another attach. Continuous typing still gives the restore timer a turn.
        long now = Environment.TickCount64;
        if (!finish && _restoreTimerArmed && now - _lastRestoreSlice < 120 &&
            ActiveSurface() is { } pane && Interlocked.Read(ref pane.InputPaintDeadline) > now) return;
        _lastRestoreSlice = now;
        if (finish) _startupJournal?.Mark("window.restore.drain", caller);
        _restoreSteps = null; // synchronous Win32 messages can reenter this window
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        bool completed = false;
        try
        {
            while (steps.MoveNext())
            {
                if (finish) continue;
                if (_restoreTimerArmed && _restoreInventory is null && System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds < 5) continue;
                if (!_restoreTimerArmed)
                    _restoreTimerArmed = Win32.SetTimer(_hwnd, RestoreTimer, 10, IntPtr.Zero) != IntPtr.Zero;
                if (!_restoreTimerArmed) continue;
                _restoreSteps = steps;
                // A rendered frame releases the host's credit to publish the next one.
                // A synchronous paint inside a step can have validated the opening
                // screen. Invalidate again now that the active surface is complete.
                Win32.InvalidateRect(_hwnd, IntPtr.Zero, false);
                Win32.UpdateWindow(_hwnd);
                return;
            }
            completed = true;
        }
        finally
        {
            if (_restoreSteps is null)
            {
                steps.Dispose();
                Win32.KillTimer(_hwnd, RestoreTimer);
                _restoreTimerArmed = false;
                _restoreDone!.TrySetResult(completed);
            }
        }
    }
}
