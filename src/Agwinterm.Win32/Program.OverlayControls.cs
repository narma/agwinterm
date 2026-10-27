using System.Globalization;
using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Win32;

internal partial class Program
{
    public string SessionOverlay(string? target, string action, string? command, int sizePercent, bool wait, bool block,
        string? pane, OverlayTextArgs text, OverlayOptions options)
    {
        TaskCompletionSource<string>? done = null;
        string? checkedCwd = options.Cwd;
        Ses? cwdSession = null;
        ShellProfile? cwdProfile = null;
        if (action == "open" && checkedCwd is not null)
        {
            // WSL may be cold: resolve on the UI, probe on the pipe thread, then
            // revalidate the owner/profile before any destructive slot change.
            (cwdSession, cwdProfile) = InvokeOnUiQueued(() =>
            {
                var candidate = FindSesForTarget(target);
                return (candidate, candidate is null ? null : ResolveProfile(candidate.ProfileName));
            });
            if (cwdProfile is not null && WslIntegration.IsWsl(cwdProfile.Command))
            {
                try { checkedCwd = WslIntegration.ValidateDirectory(cwdProfile, checkedCwd); }
                catch (Exception ex) when (ex is IOException or TimeoutException or System.ComponentModel.Win32Exception)
                { return ISessionHost.RefusePrefix + "overlay cwd is unavailable in the target WSL profile: " + ex.Message; }
            }
        }
        string reply = InvokeOnUiQueued(() =>
        {
            var hiddenTarget = target is null ? null : FindHiddenPaneByExactId(target);
            if (hiddenTarget is not null && action == "open")
                return ISessionHost.RefusePrefix + "cannot open an overlay on a hidden pane; show the pane first";
            // The shipped session-wide result is window-wide even when no session resolves.
            if (hiddenTarget is null && action == "result" && pane is null && PaneOverlayIndexOf(target) < 0) return _lastOverlayExit;
            if (target is not (null or "active") && FindStrictControlPane(target) is null)
                return NoSessionRefusal + "; unknown or ambiguous overlay target";
            Ses? session; Pane? owner = null;
            int index = PaneOverlayIndexOf(target);
            if (hiddenTarget is { ses: { } hiddenSession })
            {
                session = hiddenSession;
                owner = hiddenSession.HiddenSplit!;
                index = hiddenSession.HiddenSplitIndex;
                if (pane is not null)
                {
                    if (!OverlayPanes.TryParse(pane, out int requested, out var error)) return ISessionHost.RefusePrefix + error;
                    if (requested != index) return ISessionHost.RefusePrefix + OverlayPanes.Disagree(target!, index, requested);
                }
                if (action == "resize") return ISessionHost.RefusePrefix + OverlayPanes.ResizeWithPaneRefusal;
            }
            else if (pane is not null || index >= 0)
            {
                if (pane is not null && !OverlayPanes.TryParse(pane, out index, out var error)) return ISessionHost.RefusePrefix + error;
                var located = LocatePaneSlot(target, index, bareCloseOk: action == "close", inferred: pane is null);
                if (located.early is not null) return located.early;
                session = located.s; owner = located.p;
                if (action == "resize") return ISessionHost.RefusePrefix + (pane is not null ? OverlayPanes.ResizeWithPaneRefusal : OverlayPanes.OverlayIdResizeRefusal(target!, index));
                if (options.Percent is not null) return ISessionHost.RefusePrefix + (pane is not null ? OverlayPanes.SizeWithPaneRefusal : OverlayPanes.OverlayIdSizeRefusal(target!, index));
                if (options.HasSize) return ISessionHost.RefusePrefix + "pane overlays are always full-pane; floating dimensions are unsupported";
            }
            else
            {
                if (OverlayTargetRefusal(target) is { } refusal) return refusal;
                session = FindSesForTarget(target);
            }
            if (session is null) return action == "close" && target is null or "active" ? "no overlay" : NoSessionRefusal;
            var slot = owner?.Overlay ?? session.Overlay;
            if (action == "close")
            {
                if (slot.Term is null && (owner is not null || session.Hud is null)) return "no overlay";
                if (owner is null) CloseOverlayOf(session); else ClosePaneOverlay(session, owner);
                return "closed";
            }
            if (action is "text" or "copy") return OverlayRead(slot, action, text, owner is null ? OverlayPanes.SessionWide : index);
            if (action == "result")
            {
                lock (_overlayExitLock)
                {
                    if (slot.Term is not null && slot.LastResult == OverlayPanes.NoResult) return ISessionHost.RefusePrefix + OverlayPanes.StillRunning;
                    return slot.LastResult == OverlayPanes.NoResult ? ISessionHost.RefusePrefix + OverlayPanes.NoResult : slot.LastResult;
                }
            }
            if (action == "open")
            {
                if (owner is not null && !session.Panes.Contains(owner))
                    return ISessionHost.RefusePrefix + "cannot open an overlay on a hidden pane; show the pane first";
                if (string.IsNullOrWhiteSpace(command)) return ISessionHost.RefusePrefix + "overlay open needs a command; nothing opened";
                if (cwdSession is not null && (!ReferenceEquals(session, cwdSession) || !ReferenceEquals(ResolveProfile(session.ProfileName), cwdProfile)))
                    return ISessionHost.RefusePrefix + "overlay target or profile changed while checking cwd; nothing replaced";
                string? cwd = checkedCwd;
                if (cwd is not null && !WslIntegration.IsWsl(ResolveProfile(session.ProfileName)?.Command))
                {
                    cwd = Path.GetFullPath(WslIntegration.DecodeCwd(cwd));
                    if (!Directory.Exists(cwd)) return ISessionHost.RefusePrefix + "overlay cwd does not exist";
                }
                string opened = OverlayOpen(session, owner, command, sizePercent, block ? false : wait, launchCwd: cwd);
                if (opened.StartsWith(ISessionHost.RefusePrefix, StringComparison.Ordinal)) return opened;
                slot.WidthDip = options.Width; slot.HeightDip = options.Height;
                if (options.BackgroundColor is { } color)
                {
                    uint rgb = uint.Parse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    slot.Term!.DefaultBackground = new Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
                }
                if (options.Follow)
                {
                    if (owner is not null) session.Active = session.Panes.IndexOf(owner);
                    SetActive(session);
                    SyncSession();
                }
                if (owner is null)
                {
                    var (_, _, w, h) = OverlayRect(slot);
                    if (!Win32.IsIconic(_hwnd)) RegridTerm(slot.Term!, w, h);
                }
                done = slot.Term?.OverlayDone;
                RequestRedraw();
                return opened;
            }
            if (slot.Term is null) return ISessionHost.RefusePrefix + (action == "resize"
                ? "no overlay to resize on that target; open one first" : "no overlay");
            if (action == "resize")
            {
                var before = slot.Geometry;
                // Legacy bare resize restores full size; partial C2 requests preserve omitted axes.
                var after = before.Resize(options.Percent, options.Width, options.Height, options.Full || !options.HasSize);
                slot.SizePercent = after.Percent; slot.WidthDip = after.WidthDip; slot.HeightDip = after.HeightDip;
                var (_, _, w, h) = OverlayRect(slot);
                if (!Win32.IsIconic(_hwnd)) RegridTerm(slot.Term, w, h);
                if (before != after) SurfaceEvent("overlay", "resized", session, slot.Term);
                RequestRedraw();
                return options.Width is null && options.Height is null ? $"resized {after.Percent}%" : "resized";
            }
            bool hidden = action == "hide" || action == "toggle" && !slot.Hidden;
            if (hidden != slot.Hidden)
            {
                if (hidden) CancelOverlaySelection(slot.Term);
                slot.Hidden = hidden;
                if (owner is null && ReferenceEquals(session, _active))
                {
                    if (hidden && ReferenceEquals(_cover, slot.Term)) { _ovlOwner = null; HideCover(); }
                    else if (!hidden) { _ovlOwner = session; ShowCover(slot.Term, 3); }
                }
                if (ReferenceEquals(session, _active)) SyncSession();
                SurfaceEvent("overlay", hidden ? "hidden" : "shown", session, slot.Term, owner);
                RequestRedraw();
            }
            return slot.Hidden ? "hidden" : "shown";
        });
        if (action != "open" || !block || reply.StartsWith(ISessionHost.RefusePrefix, StringComparison.Ordinal)) return reply;
        if (done is null) throw new InvalidOperationException("overlay creation did not expose its completion source");
        try { done.Task.Wait(Timeout.Infinite, _uiGone.Token); }
        catch (OperationCanceledException) { throw new InvalidOperationException(OverlayWindowGone); }
        return done.Task.Result;
    }

    private void CancelOverlaySelection(Pane term)
    {
        if (!ReferenceEquals(_selPane, term)) return;
        _selecting = false;
        _selPane = null;
        StopSelAutoscroll();
        if (GetCapture() == _hwnd) Win32.ReleaseCapture();
    }

    private (float x, float y, float w, float h) OverlayRect(OverlaySlot slot)
    {
        var (x, y, w, h) = ContentArea();
        // Direct2D coordinates are DIP. Convert through the same geometry helper as pixel readback.
        var box = slot.Geometry.Fit(x * Scale, y * Scale, w * Scale, h * Scale, _dpi);
        return (box.x / Scale, box.y / Scale, box.w / Scale, box.h / Scale);
    }

    private IReadOnlyList<OverlayDetailSnapshot> OverlayDetails(Ses session)
    {
        var result = new List<OverlayDetailSnapshot>();
        void Add(OverlaySlot slot, Pane? owner, (float x, float y, float w, float h) box)
        {
            if (slot.Term is not { } term) return;
            bool visible = !slot.Hidden && ReferenceEquals(session, _active) &&
                (owner is null ? ReferenceEquals(_cover, term) : _cover is null);
            result.Add(new(term.Id, owner is null ? "session" : "pane", owner?.Id, slot.Hidden, visible,
                slot.Exited, box.x * Scale, box.y * Scale, box.w * Scale, box.h * Scale,
                slot.SizePercent, slot.WidthDip, slot.HeightDip));
        }
        Add(session.Overlay, null, OverlayRect(session.Overlay));
        foreach (var (pane, x, y, w, h) in PaneLayout(session)) Add(pane.Overlay, pane, (x, y, w, h));
        return result;
    }
}
