using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Win32;

internal partial class Program
{
    private sealed record OriginJob(AgtermOverlayJob Job, OriginPresentation Entry,
        AgtermPresentationBroker.Subscription Subscriber, OverlaySlot Slot);
    private readonly Dictionary<string, OriginJob> _originJobs = new(StringComparer.Ordinal);

    AgtermOverlayJob IAgtermPresentationOriginHost.OpenOverlayJob(string session, string id, JsonElement args, string account)
    {
        _ = ExportOriginTree(args);
        var scope = new AgtermOriginPresentationScope(account, Id, session, WslOriginExportRequest.Parse(args, null).Scope);
        return InvokeOnUiQueued(() =>
        {
            if (!_originJobs.TryGetValue(id, out var pending) || pending.Job.Scope != scope
                || !OriginPresentationCurrent(scope, pending.Entry)
                || !_presentationBroker.Authorizes(scope, pending.Subscriber, pending.Job.Owner))
                throw new IOException("Job owner or presenter unavailable");
            _presentationBroker.ClaimJob(pending.Job, pending.Subscriber, Environment.TickCount64);
            return pending.Job;
        });
    }

    private string? DelegateOriginJob(Ses session, Pane? pane, string command, int size, bool wait,
        string? cwd, OverlayOptions options, out TaskCompletionSource<string>? done)
    {
        done = null;
        var candidates = _originPresentations.Where(p => ReferenceEquals(p.Value.Session, session)).ToArray();
        foreach (var (scope, entry) in candidates)
        {
            if (!OriginPresentationCurrent(scope, entry) || _presentationBroker.Presenter(scope) is not { } presenter) continue;
            var slot = pane?.Overlay ?? session.Overlay;
            if (slot.Term is not null || slot.OriginJob?.Occupied == true || _webPages.ContainsKey((object?)pane ?? session)
                || pane is null && session.Hud is not null)
                return ISessionHost.RefusePrefix + "origin overlay slot occupied";
            if (options.Width is not null || options.Height is not null)
                return ISessionHost.RefusePrefix + "remote program point sizing is unsupported";
            var profile = ResolveProfile(session.ProfileName);
            string directory = cwd ?? CwdOf(pane ?? session.ActivePane) ?? "";
            directory = WslIntegration.DecodeCwd(directory);
            if (!WslLiveValidation.LinuxPath(directory) || !WslIntegration.IsWsl(profile?.Command))
                return ISessionHost.RefusePrefix + "remote program requires an origin WSL cwd and profile";
            var environment = profile?.Env is { } configured ? new Dictionary<string, string>(configured) : new Dictionary<string, string>();
            environment["AGWINTERM"] = "1";
            environment["AGWINTERM_ENABLED"] = "1";
            environment["AGWINTERM_PIPE"] = _control!.PipeName;
            environment["AGWINTERM_SESSION_ID"] = (pane ?? session.ActivePane).Id;
            environment["AGWINTERM_PANE_ID"] = (pane ?? session.ActivePane).Id;
            environment["AGWINTERM_WINDOW_ID"] = Id;
            environment["AGWINTERM_WORKSPACE_ID"] = session.Ws.Id;
            environment["TERM_PROGRAM"] = "agwinterm";
            environment["TERM_PROGRAM_VERSION"] = _termProgramVersion;
            environment["COLORTERM"] = "truecolor";
            var job = new AgtermOverlayJob(scope, presenter.Subscriber.Generation, presenter.Owner, pane?.Id,
                JsonSerializer.SerializeToElement(new { command, cwd = directory, environment }), wait, Environment.TickCount64);
            if (_originJobs.Count >= 256) return ISessionHost.RefusePrefix + "origin job limit reached";
            var pending = new OriginJob(job, entry, presenter.Subscriber, slot);
            _originJobs.Add(job.Id, pending); slot.OriginJob = job;
            done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = done;
            _ = job.Completion.ContinueWith(t =>
            {
                completion.TrySetResult(t.Result);
                Post(() =>
                {
                    RecordOriginJobResult(slot, job, pane is null, t.Result);
                    if (!job.Occupied) _originJobs.Remove(job.Id);
                });
            }, TaskScheduler.Default);
            if (pane is null) _lastOverlayExit = "no overlay"; else slot.LastResult = OverlayPanes.NoResult;
            var wire = JsonSerializer.SerializeToElement(new { job = job.Id,
                pane = pane is null ? (Guid?)null : entry.Mapping[pane.Id], sizePercent = size == 0 ? (int?)null : size,
                backgroundColor = options.BackgroundColor, follow = options.Follow, wait });
            if (!_presentationBroker.SendPresenter(scope, presenter.Subscriber, presenter.Owner, "overlay.request", wire)) job.Close("refused");
            _ = Task.Delay(15000, _uiGone.Token).ContinueWith(_ => Post(() => job.Expire(Environment.TickCount64)), TaskScheduler.Default);
            return job.Id;
        }
        return null;
    }

    private void RecordOriginJobResult(OverlaySlot slot, AgtermOverlayJob job, bool sessionWide, string result)
    {
        lock (_overlayExitLock)
        {
            if (!ReferenceEquals(slot.OriginJob, job) || slot.Term is not null) return;
            if (sessionWide) _lastOverlayExit = result; else slot.LastResult = result;
        }
    }

    private string? OriginJobControl(OverlaySlot slot, string action, int size, OverlayOptions options)
    {
        if (slot.OriginJob is not { } job || !job.Occupied) return null;
        if (action == "result") return job.Result ?? ISessionHost.RefusePrefix + OverlayPanes.StillRunning;
        if (action is not ("close" or "resize")) return ISessionHost.RefusePrefix + "operation unsupported for a delegated origin job";
        if (action == "resize" && (options.Width is not null || options.Height is not null))
            return ISessionHost.RefusePrefix + "remote program point sizing is unsupported";
        bool sent = _originJobs.TryGetValue(job.Id, out var pending)
            && _presentationBroker.SendPresenter(job.Scope, pending.Subscriber, job.Owner, action == "close" ? "overlay.close" : "overlay.resize",
                JsonSerializer.SerializeToElement(new { job = job.Id, sizePercent = size == 0 ? (int?)null : size }));
        if (action == "resize" && !sent) return ISessionHost.RefusePrefix + "job presenter unavailable";
        if (action == "close") { job.Close(); _originJobs.Remove(job.Id); }
        return action == "close" ? "closed" : "resized";
    }

    private void ReceiveOriginJob(AgtermOriginPresentationScope scope, AgtermPresentationBroker.Subscription subscriber, AgtermPresentationFrame frame)
    {
        string id = frame.Body.GetProperty("job").GetString()!;
        if (!_originJobs.TryGetValue(id, out var pending) || pending.Job.Scope != scope
            || !OriginPresentationCurrent(scope, pending.Entry)
            || !ReferenceEquals(pending.Subscriber, subscriber)
            || !_presentationBroker.Authorizes(scope, subscriber, pending.Job.Owner)) return;
        pending.Job.Close(frame.Kind == "overlay.rejected" ? "refused" : "closed");
        _originJobs.Remove(id);
    }

    private void OrphanOriginJobs(AgtermOriginPresentationScope scope, AgtermPresentationBroker.Subscription subscriber)
    {
        foreach (var pending in _originJobs.Values.Where(j => j.Job.Scope == scope && ReferenceEquals(j.Subscriber, subscriber)).ToArray())
        {
            pending.Job.PresentationLost();
            if (!pending.Job.Occupied) _originJobs.Remove(pending.Job.Id);
        }
    }
}
