using Agwinterm.Core;
using Agwinterm.Pty;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

internal partial class Program : IAskHost
{
    private static readonly AskRegistry Asks = new();
    private NativeQuestion? _nativeAsk;

    public string OpenAsk(AskSpec spec, string? window, bool follow)
    {
        if (window == "quick") throw new InvalidOperationException("quick terminal has no question");
        var owner = ResolveOpen(AskOwnerId(window, true) ?? throw new InvalidOperationException("question window not found or ambiguous"))
            ?? throw new InvalidOperationException("question window not found");
        var call = new PickOpenCall<string>();
        if (!owner.Post(() => call.Run(() => owner.OpenAskCore(spec, follow, () => call.Pending), owner.AbortAsk)))
            throw new InvalidOperationException(owner.NothingApplied());
        try { return call.Task.WaitAsync(TimeSpan.FromSeconds(10), owner._uiGone.Token).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            if (!call.Withdraw()) return call.Task.GetAwaiter().GetResult(); // publication won the deadline race
            throw new InvalidOperationException("question open withdrawn; any in-flight native construction will be discarded", ex);
        }
    }

    private static string? AskOwnerId(string? selector, bool openOnly)
    {
        lock (_windowIndex)
            return PickWindowSelector.Resolve(selector, _windowIndex.Where(m => !openOnly || m.IsOpen).Select(m => m.Id), _frontmostId ?? "");
    }

    private void AbortAsk(string id)
    {
        Asks.Abort(id);
        if (_nativeAsk?.Request.Id == id) { var ui = _nativeAsk; _nativeAsk = null; ui.Dispose(); RequestRedraw(); }
    }

    private string OpenAskCore(AskSpec spec, bool follow, Func<bool> wanted)
    {
        if (!wanted()) throw new InvalidOperationException("question open was withdrawn");
        if (NativeDialogOpen) throw new InvalidOperationException("ask already pending");
        if (_setOpen || _editing is not null || _menuHwnd != IntPtr.Zero || _dashboardOpen || _dragging || GetCapture() != IntPtr.Zero)
            throw new InvalidOperationException("another modal UI operation owns this window");
        var request = Asks.Open(Id, spec) ?? throw new InvalidOperationException("ask already pending");
        bool initialized = false;
        try
        {
            ClosePalette(); CancelLeader(); ExitChromeFocus(announce: false); DismissHoverTip();
            _nativeAsk = new NativeQuestion(_hwnd, request, outcome =>
            {
                if (initialized) Asks.Resolve(request.Id, outcome); else Asks.Abort(request.Id);
                var ui = _nativeAsk;
                if (ui?.Request.Id == request.Id) { _nativeAsk = null; ui.Dispose(); }
                RequestRedraw();
            }, () => { Frontmost = this; _frontmostId = Id; }, Perf);
            if (!wanted()) throw new InvalidOperationException("question open was withdrawn");
            _nativeAsk.Show(follow);
            initialized = true;
            RequestRedraw();
            return request.Id;
        }
        catch
        {
            AbortAsk(request.Id);
            throw;
        }
    }

    private static LocatedAsk LocateAsk(string id, string? window)
    {
        var found = Asks.Find(id) ?? throw new InvalidOperationException("unknown pick: " + id);
        if (window is not null)
        {
            if (window == "quick" || AskOwnerId(window, false) != found.Window)
                throw new InvalidOperationException("pick does not belong to the selected window");
        }
        return found;
    }

    public AskOutcome ReadAsk(string id, string? window) => LocateAsk(id, window).Outcome;

    public void CancelAsk(string id, string? window)
    {
        var found = LocateAsk(id, window);
        if (found.Outcome.Result != "pending") return;
        var owner = ResolveOpen(found.Window);
        if (owner is null) return; // WM_DESTROY settled and retained it after the lookup
        try { owner.InvokeOnUiQueued(() =>
        {
            if (Asks.Resolve(id, new("cancelled", "cancel")) && owner._nativeAsk?.Request.Id == id)
            { var ui = owner._nativeAsk; owner._nativeAsk = null; ui.Dispose(); owner.RequestRedraw(); }
            return true;
        }); }
        catch when (owner._uiGone.IsCancellationRequested && Asks.Find(id)?.Outcome.Result is "answered" or "cancelled")
        { /* Owner shutdown already settled this exact retained question. */ }
    }

    private void CloseWindowQuestion()
    {
        Asks.CloseWindow(Id);
        var ui = _nativeAsk; _nativeAsk = null; ui?.Dispose();
    }

    private void CloseLibraryWindow()
    {
        // Owned windows are destroyed before the owner's WM_DESTROY notification.
        CloseWindowQuestion();
        DestroyWindow(_hwnd);
    }

    private bool QuestionMessage(uint msg, IntPtr w, IntPtr l)
    {
        if (_nativeAsk is not { } question) return false;
        switch (PickInputPolicy.Route(msg))
        {
            case PickInputRoute.Ignore: return true;
            case PickInputRoute.Drop: DragFinish(w); return true;
            case PickInputRoute.Keyboard: question.Forward(msg, w, l); return true;
            case PickInputRoute.Focus: question.FocusIfForeground(); return true;
            default: return false;
        }
    }
}
