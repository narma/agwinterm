using System.Drawing;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;

namespace Agwinterm.Win32;

/// <summary>Optional STA browser boundary. Construct only after a validated web request.
/// No browser types belong in the terminal model, persistence, or PTY process.</summary>
internal sealed partial class WebViewHost : IDisposable
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(int key);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);
    private static bool ClipboardHasFiles() => IsClipboardFormatAvailable(15) ||
        IsClipboardFormatAvailable(2) || IsClipboardFormatAvailable(8) || IsClipboardFormatAvailable(17);
    private sealed class EnvironmentLease(Task<CoreWebView2Environment> creation)
    {
        public readonly Task<CoreWebView2Environment> Creation = creation;
        public int Users;
    }
    private static readonly Dictionary<string, EnvironmentLease> Environments = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly SynchronizationContext _context;
    private CoreWebView2Controller? _controller;
    private bool _disposed;
    private bool _initializing;
    private bool _persistent;
    private EnvironmentLease? _environment;
    private string? _dataDirectory;
    public bool HasFocus { get; private set; }
    public Action<CoreWebView2AcceleratorKeyPressedEventArgs>? Accelerator { get; set; }
    public Action<double>? ZoomChanged { get; set; }
    public Action? Focused { get; set; }
    public Action<CoreWebView2MoveFocusRequestedEventArgs>? MoveFocusRequested { get; set; }

    public WebViewHost(Func<Action, bool> post)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Web pages require the window's STA thread");
        _context = new WindowContext(post);
    }

    // Kept out of terminal startup and non-inlined so the SDK and native loader are lazy.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string RuntimeVersion()
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            throw new InvalidOperationException("WebView2 Evergreen Runtime or its loader is unavailable. Run agwintermctl install webview2 explicitly, or install the Microsoft Evergreen Standalone Runtime offline, then retry. Terminal operation does not require it.", ex);
        }
    }

    public Task CreateAsync(IntPtr parent, string dataDirectory, string pageId, CancellationToken cancellation, bool persistent = false)
        => RunAsync(() => CreateCoreAsync(parent, dataDirectory, pageId, cancellation, persistent));

    public Task RunAsync(Func<Task> action)
    {
        CheckThread();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(_context);
        try { return action(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private async Task CreateCoreAsync(IntPtr parent, string dataDirectory, string pageId, CancellationToken cancellation, bool persistent)
    {
        cancellation.ThrowIfCancellationRequested();
        if (_disposed) throw new OperationCanceledException();
        if (_initializing) throw new InvalidOperationException("A browser host can initialize only once");
        _initializing = true;
        _persistent = persistent;
        if (!persistent) await PrepareProfileAsync(dataDirectory, pageId);
        cancellation.ThrowIfCancellationRequested();
        if (_disposed) throw new OperationCanceledException();
        if (!Environments.TryGetValue(dataDirectory, out var lease))
            Environments[dataDirectory] = lease = new(CreateEnvironmentAsync(dataDirectory));
        _environment = lease; _dataDirectory = dataDirectory; lease.Users++;
        CoreWebView2Environment environment;
        try { environment = await lease.Creation; }
        catch { ReleaseEnvironment(); throw; }
        cancellation.ThrowIfCancellationRequested();
        if (_disposed) throw new OperationCanceledException();
        var options = environment.CreateCoreWebView2ControllerOptions();
        options.ProfileName = persistent ? "saved" : "page-" + pageId;
        options.IsInPrivateModeEnabled = !persistent;
        var controller = await environment.CreateCoreWebView2ControllerAsync(parent, options);
        if (_disposed || cancellation.IsCancellationRequested)
        {
            if (!persistent) controller.CoreWebView2.Profile.Delete();
            controller.Close();
            throw new OperationCanceledException(cancellation);
        }
        _controller = controller;
        controller.GotFocus += (_, _) => { HasFocus = true; Focused?.Invoke(); };
        controller.LostFocus += (_, _) => HasFocus = false;
        controller.AcceleratorKeyPressed += (_, e) =>
        {
            if (e.KeyEventKind is CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown &&
                (e.VirtualKey == 0x56 && (GetKeyState(0x11) & 0x8000) != 0 ||
                 e.VirtualKey == 0x2d && (GetKeyState(0x10) & 0x8000) != 0) && ClipboardHasFiles())
            { e.Handled = true; return; }
            Accelerator?.Invoke(e);
        };
        controller.MoveFocusRequested += (_, e) => MoveFocusRequested?.Invoke(e);
        controller.ZoomFactorChanged += (_, _) => ZoomChanged?.Invoke(controller.ZoomFactor);
        controller.IsVisible = false;
        controller.AllowExternalDrop = false;
        var settings = controller.CoreWebView2.Settings;
        settings.IsScriptEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        controller.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        controller.CoreWebView2.DownloadStarting += (_, e) => e.Cancel = true;
        controller.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
        controller.CoreWebView2.ContextMenuRequested += (_, e) =>
        {
            if (!ClipboardHasFiles()) return;
            foreach (var item in e.MenuItems)
                if (item.Name is "paste" or "pasteAndMatchStyle") item.IsEnabled = false;
        };
        // Intercept at the browser boundary; disabling script or permissions alone
        // does not prevent <input type=file> from opening a native picker.
        await controller.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.setInterceptFileChooserDialog", "{\"enabled\":true}");
        await controller.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("""
            window.addEventListener('paste', e => {
              if (e.clipboardData && (e.clipboardData.files.length ||
                  Array.from(e.clipboardData.items).some(i => i.kind === 'file'))) {
                e.preventDefault(); e.stopImmediatePropagation();
              }
            }, true);
            """);
    }

    public CoreWebView2 Page { get { CheckThread(); return _controller?.CoreWebView2 ?? throw new InvalidOperationException("Page is not initialized"); } }
    public void SetZoom(double zoom) { CheckThread(); if (_controller is { } controller) controller.ZoomFactor = zoom; }
    public void Present(Rectangle bounds, bool visible)
    {
        CheckThread();
        if (_controller is not { } controller) return;
        controller.Bounds = bounds;
        controller.IsVisible = visible && bounds.Width > 0 && bounds.Height > 0;
    }
    public void Focus(CoreWebView2MoveFocusReason reason = CoreWebView2MoveFocusReason.Programmatic) { CheckThread(); _controller?.MoveFocus(reason); }
    public void Dispose()
    {
        CheckThread();
        _disposed = true;
        var controller = _controller;
        _controller = null;
        HasFocus = false;
        ReleaseProfile();
        try
        {
            if (controller is not null)
            {
                try { if (!_persistent) controller.CoreWebView2.Profile.Delete(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
                finally
                {
                    try { controller.Close(); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
                }
            }
        }
        finally { ReleaseEnvironment(); }
    }
    private void ReleaseEnvironment()
    {
        if (_environment is not { } lease) return;
        _environment = null;
        if (--lease.Users == 0 && Environments.GetValueOrDefault(_dataDirectory!) == lease)
            Environments.Remove(_dataDirectory!);
        _dataDirectory = null;
    }
    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Browser access must stay on the window thread");
    }
    private sealed class WindowContext(Func<Action, bool> post) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            if (!post(() =>
            {
                var previous = Current;
                SetSynchronizationContext(this);
                try { callback(state); }
                finally { SetSynchronizationContext(previous); }
            })) throw new InvalidOperationException("The browser owner window has closed");
        }
        public override SynchronizationContext CreateCopy() => this;
    }
}
