using Agwinterm.Core;
using Agwinterm.Pty;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

internal partial class Program : IAskHost
{
    private static readonly AskRegistry Asks = new();
    private NativeQuestion? _nativeAsk;

    public string OpenAsk(AskSpec spec, string? window, bool follow, string? session = null)
    {
        if (window == "quick") throw new InvalidOperationException("quick terminal has no question");
        var owner = ResolveOpen(AskOwnerId(window, true) ?? throw new InvalidOperationException("question window not found or ambiguous"))
            ?? throw new InvalidOperationException("question window not found");
        var call = new PickOpenCall<string>();
        if (!owner.Post(() => call.Run(() => owner.OpenAskCore(spec, follow, () => call.Pending, session: session), owner.AbortAsk)))
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
        FinishOriginQuestion(id);
        Asks.Abort(id);
        if (_nativeAsk?.Request.Id == id) { var ui = _nativeAsk; _nativeAsk = null; ui.Dispose(); RequestRedraw(); }
    }

    private string OpenAskCore(AskSpec spec, bool follow, Func<bool> wanted, Action<AskOutcome>? completed = null, string? session = null)
    {
        if (!wanted()) throw new InvalidOperationException("question open was withdrawn");
        if (session is not null)
        {
            if (!Guid.TryParseExact(session, "D", out var id) || id == Guid.Empty)
                throw new InvalidOperationException("question target requires an exact session ID");
            var selected = AllSessions().SingleOrDefault(s => !s.Closing && Guid.TryParse(s.Id, out var candidate) && candidate == id)
                ?? throw new InvalidOperationException("question session not found in selected window");
            session = selected.Id;
        }
        if (NativeDialogOpen) throw new InvalidOperationException("ask already pending");
        if (_setOpen || _editing is not null || _menuHwnd != IntPtr.Zero || _dashboardOpen || _dragging || GetCapture() != IntPtr.Zero)
            throw new InvalidOperationException("another modal UI operation owns this window");
        var request = Asks.Open(Id, spec, session) ?? throw new InvalidOperationException("ask already pending");
        if (TryDelegateOriginQuestion(request, completed)) return request.Id;
        return ShowLocalQuestion(request, follow, wanted, completed);
    }

    private string ShowLocalQuestion(PendingAsk request, bool follow, Func<bool> wanted, Action<AskOutcome>? completed = null, bool retainOnFailure = false)
    {
        bool initialized = false;
        try
        {
            ClosePalette(); CancelLeader(); ExitChromeFocus(announce: false); DismissHoverTip();
            _nativeAsk = new NativeQuestion(_hwnd, request, outcome =>
            {
                if (initialized) Asks.Resolve(request.Id, outcome);
                else if (retainOnFailure) Asks.Resolve(request.Id, new("cancelled", "closed"));
                else Asks.Abort(request.Id);
                var ui = _nativeAsk;
                if (ui?.Request.Id == request.Id) { _nativeAsk = null; ui.Dispose(); }
                if (initialized && Asks.Find(request.Id) is { } settled) completed?.Invoke(settled.Outcome);
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
            if (retainOnFailure)
            {
                Asks.Resolve(request.Id, new("cancelled", "closed"));
                if (_nativeAsk?.Request.Id == request.Id) { var ui = _nativeAsk; _nativeAsk = null; ui.Dispose(); }
            }
            else AbortAsk(request.Id);
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
            owner.FinishOriginQuestion(id);
            if (Asks.Resolve(id, new("cancelled", "cancel")) && owner._nativeAsk?.Request.Id == id)
            { var ui = owner._nativeAsk; owner._nativeAsk = null; ui.Dispose(); owner.RequestRedraw(); }
            return true;
        }); }
        catch when (owner._uiGone.IsCancellationRequested && Asks.Find(id)?.Outcome.Result is "answered" or "cancelled")
        { /* Owner shutdown already settled this exact retained question. */ }
    }

    private void CloseWindowQuestion()
    {
        if (_originQuestion is { } delegated) FinishOriginQuestion(delegated.Request.Id);
        Asks.CloseWindow(Id);
        var ui = _nativeAsk; _nativeAsk = null; ui?.Dispose();
    }

    private void CloseSessionQuestion(Ses session)
    {
        var id = Asks.CloseSession(Id, session.Id);
        if (id is not null) FinishOriginQuestion(id);
        if (id is not null && _nativeAsk?.Request.Id == id)
        { var ui = _nativeAsk; _nativeAsk = null; ui.Dispose(); RequestRedraw(); }
    }

    private void CloseLibraryWindow()
    {
        if(ParkJournalOwnerWindow()) return;
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
