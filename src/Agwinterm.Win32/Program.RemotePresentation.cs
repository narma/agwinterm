using System.Text.Json;
using Agwinterm.Pty;
using Agwinterm.Core;

namespace Agwinterm.Win32;

internal partial class Program : IAgtermPresentationOriginHost
{
    private readonly AgtermPresentationBroker _presentationBroker = new();
    private static readonly string[] PassiveKinds = ["layout", "status", "context", "hud", "notify", "sound", "ask.form", "ask.other"];
    private sealed record OriginPresentation(Ses Session, Pane[] Panes, WslLiveBinding[] Bindings, Dictionary<string, Guid> Mapping);
    private readonly Dictionary<AgtermOriginPresentationScope, OriginPresentation> _originPresentations = new();

    AgtermOriginPresentationLease IAgtermPresentationOriginHost.OpenPresentation(string sessionId, JsonElement args,
        string account, AgtermPresentationHello hello)
    {
        // Fresh native probe plus exact UI capture uses the same export boundary as stage 3.
        var projection = (JsonElement)ExportOriginTree(args);
        var session = projection.GetProperty("remote").GetProperty("sessions").EnumerateArray()
            .SingleOrDefault(s => s.GetProperty("id").GetString() == sessionId);
        if (session.ValueKind == JsonValueKind.Undefined) throw new IOException("Exact exported session unavailable");
        var request = WslOriginExportRequest.Parse(args, null);
        var paneIds = session.GetProperty("panes").EnumerateArray()
            .Select(p => new AgtermPane(p.GetProperty("pane").GetString()!, p.GetProperty("daemon").GetString()!).OriginPaneId!.Value).ToArray();
        return InvokeOnUiQueued(() =>
        {
            if (!OriginPresentationAliveOnUi(sessionId, request.Scope)) throw new IOException("Origin retired during subscription");
            var scope = new AgtermOriginPresentationScope(account, Id, sessionId, request.Scope);
            var owner = AllSessions().Single(s => s.Id == sessionId);
            var exact = owner.AllPanes.ToArray();
            var bindings = exact.Select(p => RestoreState.LoadLiveBinding(p.LiveBinding, out _)!).ToArray();
            // Verify the fresh native projection still describes these exact daemon identities.
            var mapped = bindings.Select(b => new AgtermPane("left", b.Identity!.Name).OriginPaneId!.Value).ToArray();
            if (!mapped.SequenceEqual(paneIds)) throw new IOException("Origin changed during subscription");
            if (_originPresentations.TryGetValue(scope, out var previous) && !OriginPresentationCurrent(scope, previous))
                _presentationBroker.Retire(scope);
            var entry = new OriginPresentation(owner, exact, bindings, exact.Select((p, i) => (p.Id, Origin: mapped[i])).ToDictionary(p => p.Id, p => p.Origin));
            _originPresentations[scope] = entry;
            _presentationBroker.Register(scope, OriginPresentationSnapshot(entry), PassiveKinds, presenterReady: true);
            var subscription = _presentationBroker.Subscribe(scope, hello);
            subscription.Closed += () => Post(() => ReturnOriginQuestion(scope, subscription));
            return new AgtermOriginPresentationLease(subscription,
                () => InvokeOnUiQueued(() => OriginPresentationCurrent(scope, entry)),
                (frame, ct) => PresentationUiAsync(() => ReceiveOriginQuestion(scope, subscription, entry, frame), ct));
        });
    }

    private bool OriginPresentationCurrent(AgtermOriginPresentationScope scope, OriginPresentation entry) =>
        OriginPresentationAliveOnUi(scope.Session, scope.Origin) && AllSessions().Contains(entry.Session)
        && entry.Session.AllPanes.Count() == entry.Panes.Length && entry.Panes.Select((p, i) =>
            entry.Session.AllPanes.Contains(p) && RestoreState.LoadLiveBinding(p.LiveBinding, out _) is { } binding
            && binding.BindingId == entry.Bindings[i].BindingId && binding.Identity == entry.Bindings[i].Identity).All(ok => ok);

    private JsonElement OriginPresentationSnapshot(OriginPresentation entry)
    {
        var session = entry.Session;
        var panes = session.AllPanes.ToArray();
        var (status, changedAt) = StatusAggregate.WinnerAndChangedAt(panes.Select(p => p.S));
        var hud = session.HudMirrored ? null : session.Hud;
        object? wireHud = hud is null ? null : new
        {
            spec = new { message = hud.Message, detail = hud.Detail, spinner = hud.Spinner == "none" ? null : hud.Spinner,
                backgroundColor = hud.BackgroundColor, textColor = hud.TextColor, sizePercent = hud.SizePercent,
                position = hud.Position, hideAfter = hud.HideAfter, sticky = hud.Sticky, frame = hud.Frame, fontSize = hud.FontSize, markdown = hud.Markdown },
            pane = hud.AnchorPaneId is { } anchor ? (Guid?)entry.Mapping[anchor] : null,
            generation = session.OriginHudGeneration,
            remaining = session.HudDeadline is { } deadline ? (double?)Math.Max(0, (deadline - Environment.TickCount64) / 1000.0) : null
        };
        return JsonSerializer.SerializeToElement(new
        {
            layout = new { panes = panes.Select(p => entry.Mapping[p.Id]).ToArray(), primary = entry.Mapping[panes[0].Id],
                axis = panes.Length == 2 ? session.Axis : null, shown = session.Panes.Count == 2 },
            status = status == AgentStatus.Idle ? null : new { status = status.ToString().ToLowerInvariant(), blink = panes.Any(p => p.S.Blink && p.S.Status != AgentStatus.Idle), changedAt },
            context = session.Context, hud = wireHud
        });
    }

    private void PublishOriginPresentation(Ses session, string kind, JsonElement? live = null, bool mirrored = false)
    {
        if (mirrored) return;
        foreach (var (scope, entry) in _originPresentations.Where(pair => ReferenceEquals(pair.Value.Session, session)).ToArray())
        {
            if (!OriginPresentationCurrent(scope, entry)) { RetireOriginPresentation(scope); continue; }
            try
            {
                var snapshot = OriginPresentationSnapshot(entry);
                _presentationBroker.Publish(scope, snapshot, kind, live ?? snapshot.GetProperty(kind));
            }
            catch (Exception ex) when (ex is IOException or KeyNotFoundException or ArgumentException)
            { RetireOriginPresentation(scope); }
        }
    }

    private void RetireOriginPresentation(AgtermOriginPresentationScope scope)
    { _originPresentations.Remove(scope); _presentationBroker.Retire(scope); }

    private void RetireOriginPresentations(Ses session)
    {
        foreach (var scope in _originPresentations.Where(p => ReferenceEquals(p.Value.Session, session)).Select(p => p.Key).ToArray())
            RetireOriginPresentation(scope);
    }

    private void PublishOriginNotification(Ses session, Pane pane, string title, string body, bool mirrored)
    {
        if (mirrored) return;
        foreach (var (scope, entry) in _originPresentations.Where(p => ReferenceEquals(p.Value.Session, session)).ToArray())
        {
            if (!OriginPresentationCurrent(scope, entry)) { RetireOriginPresentation(scope); continue; }
            if (!entry.Mapping.TryGetValue(pane.Id, out var origin)) continue;
            try { _presentationBroker.Publish(scope, OriginPresentationSnapshot(entry), "notify",
                JsonSerializer.SerializeToElement(new { title, body, pane = origin, source = "control" })); }
            catch (IOException) { RetireOriginPresentation(scope); }
        }
    }

    private bool OriginPresentationAliveOnUi(string sessionId, WslOriginScope scope)
    {
        lock (_windowIndex) if (!_windowIndex.Any(w => w.Id == Id && w.IsOpen)) return false;
        var session = AllSessions().SingleOrDefault(s => s.Id == sessionId && !s.Closing);
        return session is not null && scope.OriginId == _appId && session.AllPanes.All(p =>
            !p.LiveClosing && !p.S.HasExited && RestoreState.LoadLiveBinding(p.LiveBinding, out _) is { IsValid: true, Ownership: "owned" } binding
            && WslOriginScope.From(binding.Endpoint) == scope);
    }

    private void RouteStatusSound(Pane pane, string? name, bool blockedDefault)
    {
        // This runs on the UI queue so ownership and presenter teardown cannot race the routing decision.
        var session = OwningSes(pane);
        if (pane.LiveClosing || session?.Closing == true) return;
        string? wireName = blockedDefault ? null : name ?? "default";
        if (session is not null && AgtermPresentationSound.Portable(wireName))
            foreach (var (scope, entry) in _originPresentations.Where(p => ReferenceEquals(p.Value.Session, session)).ToArray())
                if (OriginPresentationCurrent(scope, entry) && entry.Mapping.ContainsKey(pane.Id)
                    && _presentationBroker.SendSound(scope, wireName)) return;
        if (blockedDefault) PlayBlockedSound(); else PlayStatusSound(name);
    }
}
