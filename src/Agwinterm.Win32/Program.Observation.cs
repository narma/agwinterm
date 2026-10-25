using Agwinterm.Pty;

namespace Agwinterm.Win32;

internal partial class Program
{
    private sealed record ObservedLayout(string[] Panes, string Focus, string Axis, float[] Ratios);
    private readonly Dictionary<string, ObservedLayout> _observedLayouts = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Ses, object> _retiredObservationSessions = new();

    private void ObserveLayout(Ses ses)
    {
        if (ses.Panes.Count == 0 || _uiGone.IsCancellationRequested || _retiredObservationSessions.TryGetValue(ses, out _)) return;
        var now = new ObservedLayout(ses.Panes.Select(p => p.Id).ToArray(), ses.ActivePane.Id,
            ses.Axis, ses.Panes.Select(p => p.Ratio).ToArray());
        _observedLayouts.TryGetValue(ses.Id, out var before);
        _observedLayouts[ses.Id] = now;
        Observation.SetMembership(ses.Id, now.Panes);
        if (before is null) return;
        if (before.Focus != now.Focus)
            Observation.Publish("focus", new Dictionary<string, object?>
            {
                ["action"] = "changed", ["sessionId"] = ses.Id, ["paneId"] = now.Focus, ["previousPaneId"] = before.Focus
            });
        string? action = now.Panes.Length > before.Panes.Length ? "opened"
            : now.Panes.Length < before.Panes.Length ? "closed"
            : !now.Panes.SequenceEqual(before.Panes) ? "swapped"
            : now.Axis != before.Axis && now.Panes.Length > 1 ? "axis-changed"
            : !now.Ratios.SequenceEqual(before.Ratios) && now.Panes.Length > 1 ? "resized" : null;
        if (action is null) return;
        var fields = new Dictionary<string, object?>
        {
            ["action"] = action, ["sessionId"] = ses.Id, ["paneId"] = now.Focus,
            ["paneIds"] = now.Panes, ["focusedPane"] = ses.Active, ["axis"] = now.Axis
        };
        if (now.Panes.Length > 1) fields["ratios"] = now.Ratios;
        if (action == "closed") fields["removedPaneId"] = before.Panes.Except(now.Panes).Single();
        Observation.Publish("split", fields);
    }

    private void SurfaceEvent(string type, string action, Ses ses, Pane? surface = null,
        Pane? owner = null, string? reason = null, int? exitCode = null)
    {
        var fields = new Dictionary<string, object?> { ["action"] = action, ["sessionId"] = ses.Id };
        if (surface is not null) fields["paneId"] = surface.Id;
        if (type is "overlay" or "hud") fields["slot"] = owner is null ? "session" : "pane";
        if (type == "overlay" && surface is not null) fields["overlayId"] = surface.Id;
        if (owner is not null) fields["ownerPaneId"] = owner.Id;
        if (reason is not null) fields["reason"] = reason;
        if (exitCode is not null) fields["exitCode"] = exitCode;
        Observation.Publish(type, fields);
    }

    private void ScratchVisibility(Pane? pane, string action)
    {
        if (pane is null) return;
        var owner = AllSessions().FirstOrDefault(s => ReferenceEquals(s.Scratch, pane));
        if (owner is not null) SurfaceEvent("scratch", action, owner, pane);
    }

    private void EndObservation()
    {
        if (_observation is not { } observation) return;
        foreach (var session in AllSessions())
        {
            if (session.Hud is not null) SurfaceEvent("hud", "closed", session, reason: "owner-removed");
            if (session.Scratch is { } scratch) SurfaceEvent("scratch", "closed", session, scratch);
            if (session.Overlay.Term is { } overlay) SurfaceEvent("overlay", "closed", session, overlay, reason: "owner-removed");
            foreach (var pane in session.Panes)
                if (pane.Overlay.Term is { } term) SurfaceEvent("overlay", "closed", session, term, pane, "owner-removed");
        }
        if (_active is { } active)
            observation.Publish("selection", new Dictionary<string, object?> { ["action"] = "cleared", ["previousSessionId"] = active.Id });
        observation.Close();
        ObservationHeads[observation.Window] = observation.LastSequence;
    }
}
