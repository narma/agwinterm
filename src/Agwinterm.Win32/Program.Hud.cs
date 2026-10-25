using System.Globalization;
using System.Numerics;
using Agwinterm.Pty;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

internal partial class Program
{
    private const int HudTimer = 14;

    public string SessionHud(string? target, string action, HudSpec? spec) => InvokeOnUiQueued(() =>
    {
        // Session identity must be explicit if a split/auxiliary pane was named. A HUD is not
        // a pane overlay and has no PTY id of its own. Resolve/check/write in this same UI hop.
        var ses = target is null or "active" ? _active : FindSessionByExactId(
            SessionHuds.ResolveTarget(target, AllSessions().Select(s => new SessionHuds.Target(s.Id, s.Name,
                s.Panes.Select(p => p.Id).Concat(s.Panes.Select(p => p.Overlay.Term?.Id).OfType<string>())
                    .Concat(new[] { s.Scratch?.Id, s.Overlay.Term?.Id }.OfType<string>()))),
                new[] { _quick?.Id }.OfType<string>()) ?? "");
        if (ses is null) return ISessionHost.RefusePrefix + "hud: no session matches that target";
        if (target is null or "active")
        {
            if (_cover is not null) return ISessionHost.RefusePrefix + "hud: active surface is a cover; pass the session id";
        }
        if (action == "close") ClearHud(ses); // never touches a program overlay
        else
        {
            if (ses.Overlay.Term is not null) return ISessionHost.RefusePrefix + "hud: a program overlay occupies the slot";
            if (action == "update" && ses.Hud is null) return ISessionHost.RefusePrefix + "hud: no HUD to update";
            if (spec is null) return ISessionHost.RefusePrefix + "hud: missing message specification";
            if (spec.Pane is { } selector)
            {
                string? anchor = SessionHuds.ResolvePane(selector, ses.Panes.Select(p => p.Id).ToArray(), ses.Active);
                if (anchor is null) return ISessionHost.RefusePrefix + "hud: pane not found in the selected session";
                spec = spec with { AnchorPaneId = anchor };
            }
            var previous = ses.Hud;
            lock (_workspaces) ses.Hud = action == "update" ? spec with { BackgroundColor = ses.Hud!.BackgroundColor } : spec;
            ses.HudDeadline = SessionHuds.Deadline(Environment.TickCount64, spec.HideAfter);
            if (previous is null || previous with { Document = null } != ses.Hud with { Document = null } || spec.HideAfter > 0)
                SurfaceEvent("hud", previous is null ? "opened" : "updated", ses, anchorPaneId: ses.Hud.AnchorPaneId);
            RefreshHudTimer(); RequestRedraw();
        }
        return SessionHuds.Reply(ses.Id, ProjectHud(ses));
    });

    private void ClearHud(Ses ses, string reason = "closed")
    {
        if (ses.Hud is null) return;
        var anchor = ses.Hud.AnchorPaneId;
        lock (_workspaces) ses.Hud = null;
        ses.HudDeadline = null;
        SurfaceEvent("hud", "closed", ses, reason: reason, anchorPaneId: anchor);
        RefreshHudTimer(); RequestRedraw();
    }

    private void RefreshHudTimer()
    {
        bool spinning;
        lock (_workspaces) spinning = _workspaces.Any(w => w.Sessions.Any(s => s.Hud is not null && (s.Hud.Spinner != "none" || s.HudDeadline is not null)));
        if (spinning) SetTimer(_hwnd, (IntPtr)HudTimer, 80, IntPtr.Zero);
        else KillTimer(_hwnd, (IntPtr)HudTimer);
    }

    private void HudTick()
    {
        foreach (var session in AllSessions()) ReconcileHud(session);
        if (!_dashboardOpen && _cover is null && _active?.Hud is { Spinner: not "none" }) RequestRedraw();
    }

    private void ReconcileHud(Ses session)
    {
        if (session.Hud is not { } hud) return;
        if (hud.AnchorPaneId is { } id && !session.Panes.Any(p => p.Id == id)) ClearHud(session, "owner-removed");
        else if (session.HudDeadline is { } deadline && Environment.TickCount64 >= deadline) ClearHud(session, "expired");
    }

}
