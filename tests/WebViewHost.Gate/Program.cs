using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Agwinterm.Win32;
using Microsoft.Web.WebView2.Core;

internal static class Gate
{
    private static readonly ConcurrentQueue<Action> Queue = new();
    private static IntPtr _window;
    [StructLayout(LayoutKind.Sequential)] private struct Message
    { public IntPtr Window; public uint Id; public UIntPtr W; public IntPtr L; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window, uint id, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Message message);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    private static void PasteKey()
    {
        Check(GetForegroundWindow() == _window, "paste input is confined to the test window");
        keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(0x56, 0, 0, UIntPtr.Zero);
        keybd_event(0x56, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero);
    }
    private static bool Post(Action action) { Queue.Enqueue(action); return PostMessageW(_window, 0x8001, IntPtr.Zero, IntPtr.Zero); }
    private static void Check(bool condition, string description)
    { if (!condition) throw new Exception(description); Console.WriteLine("PASS " + description); }

    [STAThread]
    private static int Main()
    {
        _window = CreateWindowExW(0, "STATIC", "WebView2 isolated gate", 0x00CF0000, 0, 0, 800, 600, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_window == IntPtr.Zero) throw new Exception("Cannot create test HWND");
        using var dispatcher = new WebViewHost(Post);
        Task test = dispatcher.RunAsync(Run);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        try
        {
            while (!test.IsCompleted && DateTime.UtcNow < deadline)
            {
                while (PeekMessageW(out var message, IntPtr.Zero, 0, 0, 1))
                {
                    if (message.Id == 0x8001) { while (Queue.TryDequeue(out var action)) action(); }
                    else { TranslateMessage(ref message); DispatchMessageW(ref message); }
                }
                Thread.Sleep(1);
            }
            if (!test.IsCompleted) throw new TimeoutException("WebView2 gate timed out");
            test.GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { DestroyWindow(_window); }
    }

    private static async Task Load(CoreWebView2 page, Action navigate)
    {
        var loaded = new TaskCompletionSource<bool>();
        EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = (_, e) => loaded.TrySetResult(e.IsSuccess);
        page.NavigationCompleted += handler;
        try { navigate(); Check(await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15)), "navigation completes"); }
        finally { page.NavigationCompleted -= handler; }
    }

    private static async Task Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "agwinterm-web-host-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "index.html"), "<!doctype html><title>Storage gate</title>");
        string data = Path.Combine(directory, "profiles");
        Console.WriteLine("Runtime " + WebViewHost.RuntimeVersion());
        Console.WriteLine("Private artifacts " + directory);
        using var first = new WebViewHost(Post);
        await first.CreateAsync(_window, data, Guid.NewGuid().ToString("N"), CancellationToken.None);
        first.Page.Settings.IsScriptEnabled = true;
        first.Page.SetVirtualHostNameToFolderMapping("gate.invalid", directory, CoreWebView2HostResourceAccessKind.DenyCors);
        await Load(first.Page, () => first.Page.Navigate("https://gate.invalid/index.html"));
        await first.Page.ExecuteScriptAsync("localStorage.setItem('note','retained');");
        await Load(first.Page, first.Page.Reload);
        Check(await first.Page.ExecuteScriptAsync("localStorage.getItem('note')") == "\"retained\"", "same-page localStorage survives reload");
        using var second = new WebViewHost(Post);
        await second.CreateAsync(_window, data, Guid.NewGuid().ToString("N"), CancellationToken.None);
        second.Page.Settings.IsScriptEnabled = true;
        second.Page.SetVirtualHostNameToFolderMapping("gate.invalid", directory, CoreWebView2HostResourceAccessKind.DenyCors);
        await Load(second.Page, () => second.Page.Navigate("https://gate.invalid/index.html"));
        Check(await second.Page.ExecuteScriptAsync("localStorage.getItem('note')") == "null", "two profiles cannot read each other's localStorage even at the same origin");
        var permission = new TaskCompletionSource<bool>();
        first.Page.PermissionRequested += (_, e) => permission.TrySetResult(e.State == CoreWebView2PermissionState.Deny);
        ShowWindow(_window, 4);
        first.Present(new System.Drawing.Rectangle(0, 0, 800, 600), true);
        await first.Page.ExecuteScriptAsync("Notification.requestPermission();");
        Check(await permission.Task.WaitAsync(TimeSpan.FromSeconds(10)), "host denies permission requests without prompting");
        var download = new TaskCompletionSource<bool>();
        first.Page.DownloadStarting += (_, e) => download.TrySetResult(e.Cancel);
        await first.Page.ExecuteScriptAsync("(()=>{const a=document.createElement('a');a.href=URL.createObjectURL(new Blob(['test']));a.download='gate.txt';a.click();})()");
        Check(await download.Task.WaitAsync(TimeSpan.FromSeconds(10)), "host cancels automatic downloads");
        var popup = new TaskCompletionSource<bool>();
        first.Page.NewWindowRequested += (_, e) => popup.TrySetResult(e.Handled);
        await first.Page.ExecuteScriptAsync("window.open('https://gate.invalid/index.html','_blank');");
        Check(await popup.Task.WaitAsync(TimeSpan.FromSeconds(10)), "host suppresses unsolicited popup windows");
        var picker = new TaskCompletionSource<bool>();
        first.Page.GetDevToolsProtocolEventReceiver("Page.fileChooserOpened").DevToolsProtocolEventReceived += (_, _) => picker.TrySetResult(true);
        await first.Page.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
        await first.Page.ExecuteScriptAsync("(()=>{const input=document.createElement('input');input.type='file';input.id='file-gate';document.body.append(input);input.click();})()");
        Check(await picker.Task.WaitAsync(TimeSpan.FromSeconds(10)), "native file chooser is intercepted at the browser boundary");
        Check(await first.Page.ExecuteScriptAsync("document.getElementById('file-gate').files.length") == "0", "intercepted chooser grants no files");
        var oldClipboard = System.Windows.Forms.Clipboard.GetDataObject();
        var oldForeground = GetForegroundWindow();
        try
        {
            await first.Page.ExecuteScriptAsync("(()=>{const t=document.createElement('textarea');t.id='paste-gate';document.body.append(t);t.focus();document.addEventListener('paste',e=>{if(e.clipboardData.files.length)document.title='UNSAFE FILE PASTE'});})()");
            Check(SetForegroundWindow(_window), "test window takes focus for clipboard verification");
            first.Focus();
            System.Windows.Forms.Clipboard.SetText("plain text remains supported");
            PasteKey(); await Task.Delay(200);
            Check(await first.Page.ExecuteScriptAsync("document.getElementById('paste-gate').value") == "\"plain text remains supported\"", "ordinary native text paste still works");
            var files = new System.Collections.Specialized.StringCollection { Path.Combine(directory, "index.html") };
            System.Windows.Forms.Clipboard.SetFileDropList(files);
            PasteKey(); await Task.Delay(200);
            Check(first.Page.DocumentTitle != "UNSAFE FILE PASTE", "native file paste cannot reach document handlers");
            first.Page.Settings.IsScriptEnabled = false;
            await Load(first.Page, first.Page.Reload);
            await first.Page.ExecuteScriptAsync("(()=>{const input=document.createElement('input');input.type='file';input.id='no-script-file';document.body.append(input);input.focus();})()");
            PasteKey(); await Task.Delay(200);
            Check(await first.Page.ExecuteScriptAsync("document.getElementById('no-script-file').files.length") == "0", "file paste grants no files with document scripts disabled");
        }
        finally
        {
            if (oldClipboard is null) System.Windows.Forms.Clipboard.Clear();
            else System.Windows.Forms.Clipboard.SetDataObject(oldClipboard, true);
            if (GetForegroundWindow() == _window && oldForeground != IntPtr.Zero) SetForegroundWindow(oldForeground);
        }
        using var cancelled = new WebViewHost(Post);
        using var cancellation = new CancellationTokenSource();
        Task pending = cancelled.CreateAsync(_window, data, Guid.NewGuid().ToString("N"), cancellation.Token);
        cancellation.Cancel(); cancelled.Dispose();
        try { await pending; throw new Exception("Creation succeeded after cancellation"); }
        catch (OperationCanceledException) { Console.WriteLine("PASS close during controller creation cancels and disposes late controller"); }
        second.Dispose(); first.Dispose();
        string profileRoot = Path.Combine(data, "EBWebView");
        for (int i = 0; i < 100 && Directory.EnumerateDirectories(profileRoot, "WV2Profile_page-*").Any(); i++)
            await Task.Delay(100);
        Check(!Directory.EnumerateDirectories(profileRoot, "WV2Profile_page-*").Any(), "closed page profile directories are removed after browser exit");
        Check(await Task.Run(() => true), "message pump remains responsive after cleanup");
    }
}
