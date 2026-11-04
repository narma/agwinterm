using System.Text;
using System.Text.Json;
using Agwinterm.Pty;
using Agwinterm.Core;

namespace Agwinterm.Win32;

internal partial class Program : IWslLiveHost
{
    private readonly WslLiveOperations _liveOperations = new();

    private List<PalItem> BuildLiveMenu(string paneId)
    {
        var (_, pane) = ExactLivePane(paneId);
        if (AgtermRemoteBinding.IsRemote(pane.LiveBinding)) return BuildRemoteMenu(pane);
        var binding = RestoreState.LoadLiveBinding(pane.LiveBinding, out var error);
        var menu = new List<PalItem> { new() { Label = binding is null ? "Unavailable: " + error
            : $"{binding.Ownership} · {binding.Recovery} · {pane.LiveRole?.Role ?? "unknown"}" } };
        if (binding is null) return menu;
        void Add(string label, string verb, object args) => menu.Add(new PalItem { Label = label, Run = () =>
        {
            _ = Task.Run(() =>
            {
                try
                {
                    InvokeOnUiQueued(() =>
                    {
                        if (RestoreState.LoadLiveBinding(ExactLivePane(paneId).Pane.LiveBinding, out _)?.BindingId != binding.BindingId)
                            throw new InvalidOperationException("The pane's workload changed; reopen its menu");
                        return true;
                    });
                    LiveCommand("zmx." + verb, JsonSerializer.SerializeToElement(args), paneId);
                }
                catch (Exception ex) { Post(() => ShowToast(ex.Message, 7000)); }
            });
        } });
        Add("Retry existing workload", "retry", new { });
        Add("Detach — keep workload running", "detach", new { });
        if (binding.Recovery == "missing" && binding.Ownership == "owned")
        {
            Add("Fresh start — profile shell", "restart", new { source = "shell" });
            if (!string.IsNullOrWhiteSpace(pane.AgentResume)) Add("Fresh start — saved agent", "restart", new { source = "agent" });
            if (!string.IsNullOrWhiteSpace(pane.RestoreCommand)) Add("Fresh start — pinned command", "restart", new { source = "pin" });
            if (_config.RestoreCommands && !string.IsNullOrWhiteSpace(pane.CapturedCommand)) Add("Fresh start — captured command", "restart", new { source = "captured" });
        }
        else if (binding.Identity is not null)
        {
            Add("Take leadership for this pane", "lead", new { });
            if (binding.Ownership == "owned") Add("Terminate owned workload — all clients", "terminate", new { force = true });
        }
        return menu;
    }

    private (Ses Session, Pane Pane) ExactLivePane(string? target)
    {
        foreach (var session in AllSessions())
            foreach (var pane in session.AllPanes)
                if (pane.Id == target && RestoreState.HasLiveBinding(pane.LiveBinding)) return (session, pane);
        throw new InvalidOperationException("Exact live pane was not found in this window");
    }

    public JsonElement? LiveTerminalCommand(string command, JsonElement args, ISession session)
    {
        if (RemoteTerminalCommand(command, args, session) is { } remoteResult) return remoteResult;
        var binding = InvokeOnUiQueued(() =>
        {
            var pane = AllSessions().SelectMany(s => s.AllPanes).FirstOrDefault(p => ReferenceEquals(p.S, session));
            if (pane is null || !RestoreState.HasLiveBinding(pane.LiveBinding)) return null;
            if (pane.LiveClosing || command == "session.type" && pane.ReadOnly) throw new InvalidOperationException("Pane is closing or readonly");
            var saved = RestoreState.LoadLiveBinding(pane.LiveBinding, out var error) ?? throw new InvalidOperationException(error);
            if (saved.Identity is null || saved.Recovery is "missing" or "uncertain" or "unavailable" or "unsupported")
                throw new InvalidOperationException("No verified live workload is available");
            return saved;
        });
        if (binding is null) return null;
        if (command == "session.type")
        {
            string text = (args.TryGetProperty("text", out var value) ? value.GetString() : "") ?? "";
            return _liveOperations.TypeAsync(binding, text.Replace("\r\n", "\r").Replace('\n', '\r')).GetAwaiter().GetResult();
        }
        bool all = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("all", out var allValue) && allValue.ValueKind == JsonValueKind.True;
        int lines = 0;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("lines", out var lineValue))
            OverlayPanes.TryLines(lineValue.GetRawText(), out lines);
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("styles", out var styles) && styles.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("The pinned daemon exposes authoritative text/cursor, not styled cell queries");
        var screen = _liveOperations.ScreenAsync(binding, all || lines > 0).GetAwaiter().GetResult();
        if (command == "surface.cursor") return screen.GetProperty("cursorColumn").Clone();
        string textResult = screen.GetProperty("text").GetString() ?? "";
        if (lines > 0) textResult = string.Join('\n', textResult.TrimEnd('\n').Split('\n').TakeLast(lines));
        return JsonSerializer.SerializeToElement(textResult.TrimEnd('\n'));
    }

    public object LiveCommand(string command, JsonElement args, string? target)
    {
        if (command.StartsWith("zmx.remote.", StringComparison.Ordinal)) return RemoteCommand(command, args, target);
        WslLiveControl.Validate(command, args, target);
        if (command == "zmx.export.tree") return ExportOriginTree(args);
        if (command == "zmx.export.ready")
        {
            _ = ExportOriginTree(args); // Same endpoint verification; no registration or UI mutation.
            return new { presentation = 1, kinds = PassiveKinds, mode = "presenter" };
        }
        if (command is "zmx.list" or "zmx.attach")
        {
            string name = args.GetProperty("profile").GetString()!;
            var profile = InvokeOnUiQueued(() => _profileCfg.Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Unknown profile"));
            var sessions = _liveOperations.ListAsync(profile, _appId).GetAwaiter().GetResult();
            if (command == "zmx.list") return new { sessions = sessions.Select(s => new { daemon = s.Daemon, incarnation = s.Incarnation,
                ownership = s.Ownership, attachments = s.Attachments, endpoint = s.Binding.Endpoint }),
                readiness = "ready", provider = "wsl-zmx", host = "server-rust", remoteAttach = false, presentation = false };
            var chosen = sessions.SingleOrDefault(s => s.Daemon == args.GetProperty("daemon").GetString()
                && s.Incarnation == args.GetProperty("incarnation").GetString()) ?? throw new InvalidOperationException("Discovered incarnation no longer matches");
            var binding = _liveOperations.BorrowAsync(chosen.Binding).GetAwaiter().GetResult();
            if (binding.Recovery != "zmx-reattached") throw new InvalidOperationException("Daemon is no longer ready");
            return InvokeOnUiQueued<object>(() =>
            {
                var id = Guid.NewGuid().ToString();
                CreateSession(id, null, null, ActiveWorkspace(), makeActive: false, profileName: profile.Name,
                    liveBinding: JsonSerializer.SerializeToElement(binding with { TransportTicket = null, AttachmentNonce = null }));
                SaveState(sync: true);
                return new { paneId = id, bindingId = binding.BindingId, ownership = binding.Ownership, recovery = "pending" };
            });
        }
        if (command == "zmx.lead") return InvokeOnUiQueued<object>(() =>
        {
            var (session, pane) = ExactLivePane(target);
            if (pane.LiveClosing || pane.Start is { IsCompleted: false }) throw new InvalidOperationException("Recovery already in progress");
            var binding = RestoreState.LoadLiveBinding(pane.LiveBinding, out var error) ?? throw new InvalidOperationException(error);
            if (binding.Identity is null || binding.Recovery is "missing" or "unsupported" or "uncertain" or "unavailable")
                throw new InvalidOperationException("A verified live attachment is required");
            var next = ReplaceLivePane(session, pane, binding with { TransportTicket = null, AttachmentNonce = null });
            next.LiveClaim = true;
            return new { paneId = pane.Id, leadership = "pending", accepted = true };
        });
        if (command == "zmx.retry") return InvokeOnUiQueued<object>(() =>
        {
            var (session, pane) = ExactLivePane(target);
            var binding = RestoreState.LoadLiveBinding(pane.LiveBinding, out var error)
                ?? throw new InvalidOperationException(error);
            if (pane.LiveClosing || pane.Start is { IsCompleted: false }) throw new InvalidOperationException("Recovery already in progress");
            if (!pane.S.HasExited && binding.Recovery is "host-adopted" or "zmx-reattached" or "fresh-started")
                return LiveReadback(pane);
            ReplaceLivePane(session, pane, binding with { TransportTicket = null });
            return new { paneId = pane.Id, bindingId = binding.BindingId, recovery = "pending", freshStart = false };
        });
        if (command is not ("zmx.detach" or "zmx.terminate" or "zmx.restart"))
            throw new InvalidOperationException("This live control is not yet available");
        var selected = InvokeOnUiQueued(() =>
        {
            var selected = ExactLivePane(target);
            if (selected.Pane.LiveClosing) throw new InvalidOperationException("Pane lifecycle operation is already in progress");
            selected.Pane.LiveClosing = true;
            selected.Pane.LiveRecovery.Cancel();
            return selected;
        });
        var p = selected.Pane;
        try
        {
            p.Start?.GetAwaiter().GetResult(); // dispatch thread, never the UI thread
            var binding = InvokeOnUiQueued(() => RestoreState.LoadLiveBinding(p.LiveBinding, out var error)
                ?? throw new InvalidOperationException(error));
            if (command == "zmx.restart")
            {
                string source = args.GetProperty("source").GetString()!;
                string? launch = InvokeOnUiQueued(() => source switch
                {
                    "shell" => null,
                    "agent" => p.AgentResume,
                    "pin" => p.RestoreCommand,
                    "captured" when _config.RestoreCommands => p.CapturedCommand,
                    _ => throw new InvalidOperationException("Selected fresh-start source is not enabled")
                });
                string promptToken = Guid.NewGuid().ToString("N");
                var completion = new ShellCompletion(promptToken, CompletionPath(promptToken));
                var result = _liveOperations.RestartAsync(binding, source, launch, pending => LiveUiAsync(() =>
                {
                    p.Completion = completion; // checkpoint the new shell identity BEFORE it can start
                    p.LiveBinding = JsonSerializer.SerializeToElement(pending);
                    if (!SaveState(sync: true)) throw new IOException("Cannot save fresh-start reservation");
                }), promptToken: completion.Token).GetAwaiter().GetResult();
                return InvokeOnUiQueued<object>(() =>
                {
                    // Keep the prior recovery option through failed/lost reservations. Only a
                    // confirmed replacement supersedes it; rollback must not destroy saved work.
                    BindAgent(p, source == "agent" ? launch : null, 0);
                    p.LiveBinding = JsonSerializer.SerializeToElement(result);
                    SaveState(sync: true);
                    var next = ReplaceLivePane(selected.Session, p, result, freshStart: true);
                    return new { paneId = next.Id, bindingId = result.BindingId, recovery = result.Recovery, source };
                });
            }
            if (command == "zmx.terminate")
            {
                if (binding.Pending) binding = _liveOperations.ReconcileAsync(binding).GetAwaiter().GetResult();
                string outcome = _liveOperations.TerminateAsync(binding).GetAwaiter().GetResult();
                if (outcome != "terminated") throw new WslRecoveryException("uncertain", "Termination is not confirmed");
                return InvokeOnUiQueued<object>(() =>
                {
                    p.S.Dispose();
                    p.LiveBinding = JsonSerializer.SerializeToElement(binding with { Recovery = "missing", TransportTicket = null });
                    p.LiveClosing = false;
                    p.LiveRecovery.Reopen();
                    ShowLiveError(p, "Owned workload terminated. Select a fresh-start source to run new work.");
                    SaveState(sync: true);
                    return LiveReadback(p);
                });
            }
            return InvokeOnUiQueued<object>(() =>
            {
                p.LiveTerminated = true; // local removal only, no owned-close termination
                if (selected.Session.AllPanes.Count() == 1) CloseSessionInternal(selected.Session, undoable: false, remember: false);
                else ClosePane(selected.Session, p);
                SaveState(sync: true);
                return new { paneId = p.Id, bindingId = binding.BindingId, detached = true, workloadPreserved = true };
            });
        }
        catch (Exception ex)
        {
            InvokeOnUiQueued(() =>
            {
                p.LiveClosing = false;
                p.LiveRecovery.Reopen();
                if (RestoreState.LoadLiveBinding(p.LiveBinding, out _) is { } current)
                    p.LiveBinding = JsonSerializer.SerializeToElement(current with { Error = ex.Message,
                        Recovery = ex is WslRecoveryException known ? known.Outcome : current.Recovery });
                ShowLiveError(p, ex.Message);
                SaveState();
                return true;
            });
            throw;
        }
    }

    private Pane ReplaceLivePane(Ses session, Pane previous, WslLiveBinding binding, bool freshStart = false)
    {
        previous.LiveClosing = true;
        previous.LiveRecovery.Cancel();
        previous.S.Dispose(); // attachment only, Linux ownership is deliberately untouched
        Observation.RemovePane(previous.Id);
        var next = CreatePane(previous.Id, session.Ws, previous.StartCwd, previous.FontSize,
            profileName: session.ProfileName, liveBinding: JsonSerializer.SerializeToElement(binding), completion: previous.Completion);
        next.LiveFreshStart = freshStart;
        next.AgentResume = previous.AgentResume;
        next.AgentResumeToken = previous.AgentResumeToken;
        next.AgentResumeGeneration = previous.AgentResumeGeneration;
        next.BindCompletionBaseline = previous.BindCompletionBaseline;
        next.RestoreCommand = previous.RestoreCommand;
        next.CapturedCommand = previous.CapturedCommand;
        next.Appearance = previous.Appearance;
        next.FontZoomed = previous.FontZoomed;
        next.Ratio = previous.Ratio;
        next.ReadOnly = previous.ReadOnly;
        next.Overlay = previous.Overlay;
        next.Unread = previous.Unread;
        next.UnreadCategory = previous.UnreadCategory;
        lock (_workspaces)
        {
            if (ReferenceEquals(session.HiddenSplit, previous)) session.HiddenSplit = next;
            else session.Panes[session.Panes.IndexOf(previous)] = next;
        }
        if (ReferenceEquals(_selPane, previous)) { _selPane = null; _selecting = false; }
        if (ReferenceEquals(_active, session)) SyncSession();
        RegridSession(session);
        SaveState();
        RequestRedraw();
        return next;
    }

    private JsonElement LiveReadback(Pane pane)
    {
        if (AgtermRemoteBinding.IsRemote(pane.LiveBinding)) return RemoteReadback(pane);
        var binding = RestoreState.LoadLiveBinding(pane.LiveBinding, out var error);
        return JsonSerializer.SerializeToElement(new { provider = "wsl-zmx", bindingId = binding?.BindingId,
            endpoint = binding?.Endpoint, daemon = binding?.Identity, ownership = binding?.Ownership,
            recovery = binding?.Recovery ?? "unavailable", pending = binding?.Pending ?? false,
            closing = pane.LiveClosing, error = pane.LiveSaveError ?? binding?.Error ?? error,
            role = pane.LiveRole?.Role ?? "unknown", roleGeneration = pane.LiveRole?.Generation,
            closeAction = binding?.Ownership == "owned" ? "terminate-owned-workload" : "disconnect-attachment",
            remoteAttach = false, presentation = false });
    }

    private bool BeginLiveClose(IEnumerable<Pane> candidates, Action resume)
    {
        var panes = candidates.Where(p => RestoreState.HasLiveBinding(p.LiveBinding) && !p.LiveTerminated).ToArray();
        if (panes.Length == 0) return false;
        if (panes.Any(p => p.LiveClosing)) return true;
        foreach (var pane in panes) { pane.LiveClosing = true; pane.LiveRecovery.Cancel(); }
        _ = FinishLiveCloseAsync(panes, resume);
        return true;
    }

    private async Task FinishLiveCloseAsync(Pane[] panes, Action resume)
    {
        try
        {
            foreach (var pane in panes)
            {
                if (pane.Start is { } start) await start.ConfigureAwait(false);
                WslLiveBinding? binding = null;
                await LiveUiAsync(() => binding = RestoreState.LoadLiveBinding(pane.LiveBinding, out _)).ConfigureAwait(false);
                if (binding is { Ownership: "owned" })
                {
                    if (binding.Pending) binding = await _liveOperations.ReconcileAsync(binding).ConfigureAwait(false);
                    if (binding.Recovery != "missing" && await _liveOperations.TerminateAsync(binding).ConfigureAwait(false) != "terminated")
                        throw new InvalidOperationException("Owned workload termination is uncertain; pane retained");
                }
                // An invalid/unavailable record grants no Linux kill authority.
                // Explicit close may remove its local error surface only.
                await LiveUiAsync(() =>
                {
                    pane.LiveTerminated = true;
                    if (binding is not null && binding.Ownership == "owned")
                        pane.LiveBinding = JsonSerializer.SerializeToElement(binding with { Recovery = "missing", TransportTicket = null });
                }).ConfigureAwait(false);
            }
            await LiveUiAsync(resume).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await LiveUiAsync(() =>
            {
                foreach (var pane in panes) { pane.LiveClosing = false; pane.LiveRecovery.Reopen(); ShowLiveError(pane, ex.Message); }
                SaveState();
            }).ConfigureAwait(false);
        }
    }

    private Task LiveUiAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { action(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } });
        return completion.Task;
    }

    private void ShowLiveError(Pane pane, string message)
    {
        lock (pane.S.SyncRoot) pane.S.Emulator.Feed(Encoding.UTF8.GetBytes("\r\n[WSL live session] " + message + "\r\n"));
        pane.LiveSaveError = message;
        RequestRedraw();
    }

    private void ConfigureLiveTitle(Pane pane, string? nonce)
    {
        lock (pane.S.SyncRoot)
        {
            pane.LiveRole = new ZmxRoleState(nonce ?? Guid.NewGuid().ToString("N"));
            if (pane.S.Emulator is not TerminalEmulator terminal) return;
            bool Consume(string title)
            {
                if (!title.StartsWith("zmx-role;", StringComparison.Ordinal)) return false;
                if (ZmxRoleNotice.Parse(title) is { } notice) pane.LiveRole.Apply(notice);
                return true;
            }
            if (Consume(terminal.Title)) terminal.OscDispatch(2, "");
            terminal.TitleFilter = Consume;
        }
    }

    private async Task StartLivePaneAsync(Pane pane, WslLiveBinding? saved, ShellProfile? profile,
        string? cwd, IReadOnlyDictionary<string, string> environment)
    {
        if (!pane.LiveRecovery.TryBegin(out long generation)) return;
        try
        {
            WslLiveBinding result;
            if (saved is null)
            {
                if (profile is null) throw new InvalidOperationException("The configured WSL profile is unavailable");
                result = await _liveOperations.CreateAsync(profile, _appId, pane.Id, cwd, environment,
                    binding => LiveUiAsync(() =>
                    {
                        if (!pane.LiveRecovery.IsCurrent(generation)) throw new OperationCanceledException("Pane is closing");
                        pane.LiveBinding = JsonSerializer.SerializeToElement(binding);
                        if (!SaveState(sync: true)) throw new IOException("Cannot save live creation reservation; no workload was started");
                    })).ConfigureAwait(false);
            }
            else result = await _liveOperations.ReconcileAsync(saved).ConfigureAwait(false);
            if (pane.LiveFreshStart && result.Recovery == "zmx-reattached") result = result with { Recovery = "fresh-started" };

            await LiveUiAsync(() => pane.LiveRecovery.Publish(generation, () =>
            {
                pane.LiveSaveError = null;
                pane.LiveBinding = JsonSerializer.SerializeToElement(result);
                SaveState(sync: true);
            })).ConfigureAwait(false);
            if (!pane.LiveRecovery.IsCurrent(generation)) return;
            if (result.Recovery is not ("fresh-started" or "zmx-reattached"))
                throw new InvalidOperationException("Workload recovery is " + result.Recovery + "; no commands were replayed");
            string nonce = Guid.NewGuid().ToString("N");
            await LiveUiAsync(() => ConfigureLiveTitle(pane, nonce)).ConfigureAwait(false);
            if (!pane.LiveRecovery.IsCurrent(generation)) return;
            string[] argv = WslLiveOperations.AttachmentArgs(result, nonce, pane.LiveClaim);
            await pane.S.StartAsync("wsl.exe", new SessionCommand("wsl.exe", argv).QuotedArgs,
                verbatimCommandLine: true).ConfigureAwait(false);
            if (!pane.LiveRecovery.IsCurrent(generation)) return;
            if (pane.S is not ServerSession server || server.HasExited || string.IsNullOrEmpty(server.CreationTicket))
                throw new InvalidOperationException("Rust WSL attachment could not start");
            await LiveUiAsync(() =>
            {
                if (!pane.LiveRecovery.IsCurrent(generation)) return;
                pane.LiveBinding = JsonSerializer.SerializeToElement(result with { TransportTicket = server.CreationTicket, AttachmentNonce = nonce });
                SaveState(sync: true);
                RequestRedraw();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await LiveUiAsync(() =>
            {
                if (!pane.LiveRecovery.IsCurrent(generation)) return;
                if (RestoreState.LoadLiveBinding(pane.LiveBinding, out _) is { } binding)
                    pane.LiveBinding = JsonSerializer.SerializeToElement(binding with { Error = ex.Message,
                        Recovery = ex is WslRecoveryException known ? known.Outcome : binding.Recovery == "missing" ? "missing" : "uncertain" });
                ShowLiveError(pane, ex.Message);
                SaveState();
            }).ConfigureAwait(false);
        }
        finally { pane.LiveRecovery.Complete(generation); }
    }
}
