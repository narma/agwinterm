using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Agwinterm.Win32;

internal partial class Program
{
    // Controller completion must outlive an individual HWND: another window may
    // still be running when a closed owner's asynchronous creation completes.
    private const uint WebDispatchMessage = 0x8003;
    private static readonly ConcurrentQueue<Action> WebContinuations = new();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint message, IntPtr w, IntPtr l);

    private static WebViewHost NewWebHost()
    {
        uint thread = GetCurrentThreadId();
        return new WebViewHost(action =>
        {
            WebContinuations.Enqueue(action);
            return PostThreadMessageW(thread, WebDispatchMessage, IntPtr.Zero, IntPtr.Zero);
        });
    }

    private static bool DispatchWebContinuation(Win32.MSG message)
    {
        if (message.hwnd != IntPtr.Zero || message.message != WebDispatchMessage) return false;
        while (WebContinuations.TryDequeue(out var continuation)) continuation();
        return true;
    }
}
