using Agwinterm.Pty;

namespace Agwinterm.Win32;

internal partial class Program
{
    private sealed record PresentationReplica(Pane[] Panes, AgtermRemoteBinding[] Bindings, CancellationTokenSource Stop)
    {
        public readonly AgtermPassivePresentation State = new();
        public bool Connected;
        public long SoundEvents, NotificationEvents;
        public AgtermPresentationSender? Sender;
        public void Cancel() { try { Stop.Cancel(); } catch (ObjectDisposedException) { } }
    }
    private readonly Dictionary<Ses, PresentationReplica> _presentationReplicas = new();

    private void RetireRemotePresentation(Ses session)
    {
        if (!_presentationReplicas.Remove(session, out var previous)) return;
        previous.Cancel();
        ClearRemotePresentation(session, previous);
        session.RemotePresentation = null;
    }

    private void EnsureRemotePresentation(Ses session)
    {
        if (_presentationReplicas.TryGetValue(session, out var existing))
        {
            // A terminal transport replacement (including explicit leadership) does not replace
            // presentation authority when every scoped workload and stable pane ID is unchanged.
            var currentPanes = session.AllPanes.ToArray();
            var rebound = existing.Panes.Select((old, i) => currentPanes.SingleOrDefault(p => p.Id == old.Id && !p.LiveClosing
                && AgtermRemoteBinding.Load(p.LiveBinding, out _) is { } binding && binding.SamePresentationOrigin(existing.Bindings[i]))).ToArray();
            if (currentPanes.Length == existing.Panes.Length && rebound.All(p => p is not null))
                for (int i = 0; i < rebound.Length; i++) existing.Panes[i] = rebound[i]!;
            if (PresentationCurrent(session, existing)) return;
            RetireRemotePresentation(session);
        }
        if (session.Closing || session.AllPanes.Any(p => p.LiveClosing)) return;
        var panes = session.AllPanes.ToArray();
        var bindings = panes.Select(p => AgtermRemoteBinding.Load(p.LiveBinding, out _)).ToArray();
        if (bindings.Length == 0 || bindings.Any(b => b is not { IsValid: true, PresentationVersion: > 0 })) return;
        var selected = bindings.Select(b => b!).ToArray(); var first = selected[0];
        if (selected.Any(b => b.OriginSession != first.OriginSession || b.Target != first.Target || b.Endpoint != first.Endpoint)) return;
        var replica = new PresentationReplica(panes, selected, CancellationTokenSource.CreateLinkedTokenSource(_uiGone.Token));
        _presentationReplicas[session] = replica; session.RemotePresentation = replica.State;
        _ = RunRemotePresentationAsync(session, replica);
    }

    private bool PresentationCurrent(Ses session, PresentationReplica replica) =>
        !_uiGone.IsCancellationRequested && !session.Closing && AllSessions().Contains(session)
        && _presentationReplicas.TryGetValue(session, out var current) && ReferenceEquals(current, replica)
        && session.AllPanes.Count() == replica.Panes.Length
        && replica.Panes.Select((p, i) => !p.LiveClosing && session.AllPanes.Contains(p)
            && AgtermRemoteBinding.Load(p.LiveBinding, out _) is { } binding
            && binding.SamePresentationOrigin(replica.Bindings[i])).All(ok => ok);

    private async Task RunRemotePresentationAsync(Ses session, PresentationReplica replica)
    {
        var ct = replica.Stop.Token; int failures = 0;
        try
        {
            while (!ct.IsCancellationRequested && failures < 5)
            {
                try
                {
                    foreach (var binding in replica.Bindings) await _remoteOperations.RevalidateAsync(binding, ct).ConfigureAwait(false);
                    await new AgtermPresentationTransport().RunAsync(replica.Bindings[0], ["status", "context", "hud", "notify", "sound", "layout", "ask.form", "ask.other"],
                        async (frame, token) => await PresentationUiAsync(() =>
                        {
                            token.ThrowIfCancellationRequested();
                            if (!PresentationCurrent(session, replica)) throw new IOException("Presentation owner replaced or closed");
                            var mapping = replica.Bindings.Select((b, i) => (Origin: b.OriginPaneId!.Value, Local: replica.Panes[i].Id))
                                .ToDictionary(p => p.Origin, p => p.Local);
                            if (ApplyRemoteQuestion(session, replica, frame, mapping)) return;
                            if (ApplyReplicaJob(session, replica, frame, mapping)) return;
                            if (frame.Kind == "sound")
                            {
                                var name = AgtermPresentationSound.Read(frame.Body);
                                if (replica.Sender?.Granted == true)
                                {
                                    replica.SoundEvents++;
                                    if (AgtermPresentationSound.ViewerSound(name, _config.BlockedSound) is { } sound) PlayStatusSound(sound);
                                }
                                return;
                            }
                            var notification = replica.State.Apply(frame, mapping, Environment.TickCount64);
                            if (frame.Kind is "snapshot" or "hud")
                            {
                                if (replica.State.Hud is not null)
                                {
                                    if (session.Hud is not null && !session.HudMirrored || session.Overlay.Term is not null || _webPages.ContainsKey(session))
                                        throw new IOException("Local overlay slot is occupied");
                                    session.Hud = replica.State.Hud; session.HudDeadline = replica.State.HudDeadline; session.HudMirrored = true;
                                }
                                else if (session.HudMirrored) ClearHud(session);
                                RefreshHudTimer();
                            }
                            if (notification is not null)
                            {
                                replica.NotificationEvents++;
                                var pane = notification.Pane is null ? replica.Panes[0] : replica.Panes.Single(p => p.Id == notification.Pane);
                                OnNotified(pane, notification.Title, notification.Body, mirrored: true);
                            }
                            session.PresentationError = null;
                            if (frame.Kind == "snapshot")
                            {
                                failures = 0; replica.Connected = true;
                                if (replica.Sender?.Mode == "presenter") replica.Sender.Send("presenter.acquire");
                            }
                            RequestRedraw();
                        }, token).ConfigureAwait(false), ct, mode: "presenter", initialized: sender => replica.Sender = sender).ConfigureAwait(false);
                    throw new IOException("Presentation stream ended");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    await PresentationUiAsync(() =>
                    {
                        if (!PresentationCurrent(session, replica)) { replica.Cancel(); return; }
                        ClearRemotePresentation(session, replica); session.PresentationError = ex.Message;
                    }, ct).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(1 << failures++), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { replica.Stop.Dispose(); }
    }

    private Task PresentationUiAsync(Action action, CancellationToken ct)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(() =>
        {
            try { ct.ThrowIfCancellationRequested(); action(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetCanceled(ct);
        return completion.Task.WaitAsync(ct);
    }

    private void ClearRemotePresentation(Ses session, PresentationReplica replica)
    {
        session.PresentationQuestion.Disconnected();
        DismissReplicaQuestion(replica);
        OrphanReplicaJobs(replica);
        replica.State.Clear();
        replica.Connected = false;
        if (session.HudMirrored) ClearHud(session);
        RequestRedraw();
    }
}
