using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Win32;

internal partial class Program
{
    private readonly AgtermRemoteOperations _remoteOperations = new();
    private readonly Dictionary<string, int> _remoteRetries = new(StringComparer.Ordinal);
    private readonly HashSet<Pane> _remoteReconnectPending = [];

    private List<PalItem> BuildRemoteMenu(Pane pane)
    {
        var binding = AgtermRemoteBinding.Load(pane.LiveBinding, out var error);
        var menu = new List<PalItem> { new() { Label = binding is null ? error! :
            $"borrowed · {binding.Recovery} · {pane.LiveRole?.Role ?? "unknown"}" } };
        if (binding is null) return menu;
        foreach (var (label, verb) in new[] { ("Retry existing remote workload", "retry"),
            ("Detach — keep origin workload running", "detach"), ("Take leadership for this pane", "lead") })
            menu.Add(new PalItem { Label = label, Run = () => _ = Task.Run(() =>
            {
                try
                {
                    InvokeOnUiQueued(() =>
                    {
                        if (AgtermRemoteBinding.Load(ExactLivePane(pane.Id).Pane.LiveBinding, out _)?.BindingId != binding.BindingId)
                            throw new InvalidOperationException("Remote binding changed; reopen its menu");
                        return true;
                    });
                    RemoteCommand("zmx.remote." + verb, default, pane.Id);
                }
                catch (Exception ex) { Post(() => ShowToast(ex.Message, 7000)); }
            }) });
        return menu;
    }

    private static JsonElement RecentRemoteBinding(JsonElement raw)
    {
        if (!AgtermRemoteBinding.IsRemote(raw)) return default;
        var binding = AgtermRemoteBinding.Load(raw, out _);
        return binding is null ? raw.Clone() : JsonSerializer.SerializeToElement(binding with
            { TransportTicket = null, AttachmentNonce = null, Recovery = "pending", Error = null });
    }

    private object RemoteCommand(string command, JsonElement args, string? target)
    {
        AgtermRemoteControl.Validate(command, args, target);
        if (command is "zmx.remote.list" or "zmx.remote.attach")
        {
            var destination = AgtermRemoteControl.Target(args);
            var inventory = AgtermSshDiscoveryClient.DiscoverAsync(destination).GetAwaiter().GetResult();
            if (command == "zmx.remote.list") return new { provider = "agterm-ssh", inventory.Endpoint, inventory.Sessions,
                terminalAttachmentVerified = false, inboundExport = false, presentation = false };
            var selected = inventory.Sessions.SingleOrDefault(s => s.Session.Id == args.GetProperty("session").GetString())
                ?? throw new InvalidOperationException("Exact origin session is missing");
            if (!selected.CanImportTerminal) throw new InvalidOperationException(selected.UnsupportedReason);
            string bridge = args.GetProperty("bridge").GetString()!;
            // All identities/topology are resolved before a single local pane is published.
            var bindings = selected.Session.Panes.Select(p => _remoteOperations.ProbeAsync(destination, bridge,
                inventory.Endpoint, selected.Session, p).GetAwaiter().GetResult()).ToArray();
            return InvokeOnUiQueued<object>(() =>
            {
                lock (_registry) if (!_registry.Values.Contains(this))
                    throw new InvalidOperationException("Selected local window closed");
                if (_sessionBackend.Name != "server-rust" || _isQuickWindow) throw new InvalidOperationException("Remote import requires a Rust-hosted ordinary window");
                var ws = ActiveWorkspace();
                var id = Guid.NewGuid().ToString();
                var session = CreateSession(id, selected.Session.Name, null, ws, false,
                    liveBinding: JsonSerializer.SerializeToElement(bindings[0]));
                try
                {
                    session.Axis = RestoreState.LoadAxis(selected.Session.SplitAxis);
                    if (bindings.Length == 2) AppendPane(session, Guid.NewGuid().ToString(), null,
                        session.Panes[0].FontSize, JsonSerializer.SerializeToElement(bindings[1]));
                    RegridSession(session);
                    if (!SaveState(sync: true)) throw new IOException("Remote checkpoint could not be saved");
                    return new { sessionId = id, originSession = selected.Session.Id,
                        panes = session.Panes.Select(p => new { paneId = p.Id, liveSession = RemoteReadback(p) }).ToArray() };
                }
                catch
                {
                    foreach (var pane in session.AllPanes) { pane.LiveClosing = true; pane.LiveRecovery.Cancel(); }
                    DisposeSessionResources(session);
                    lock (_workspaces) ws.Sessions.Remove(session);
                    SaveState();
                    throw;
                }
            });
        }
        return InvokeOnUiQueued<object>(() =>
        {
            var (session, pane) = ExactLivePane(target);
            var binding = AgtermRemoteBinding.Load(pane.LiveBinding, out var error) ?? throw new InvalidOperationException(error);
            if (pane.LiveClosing || pane.Start is { IsCompleted: false }) throw new InvalidOperationException("Remote lifecycle operation is in progress");
            _remoteRetries.Remove(binding.BindingId);
            if (command == "zmx.remote.detach")
            {
                pane.LiveClosing = true; pane.LiveTerminated = true; pane.LiveRecovery.Cancel();
                if (session.AllPanes.Count() == 1) CloseSessionInternal(session, undoable: false, remember: false);
                else ClosePane(session, pane);
                SaveState(sync: true);
                return new { detached = true, workloadPreserved = true };
            }
            if (command == "zmx.remote.retry" && !pane.S.HasExited && binding.Recovery is "host-adopted" or "remote-reattached")
                return RemoteReadback(pane);
            if (command == "zmx.remote.lead" && binding.Recovery is not ("host-adopted" or "remote-reattached"))
                throw new InvalidOperationException("A verified attachment is required before leadership");
            var next = ReplaceRemotePane(session, pane, binding, command == "zmx.remote.lead");
            return RemoteReadback(next);
        });
    }

    private Pane ReplaceRemotePane(Ses session, Pane previous, AgtermRemoteBinding binding, bool claim = false)
    {
        previous.LiveClosing = true;
        previous.LiveRecovery.Cancel();
        previous.S.Dispose();
        Observation.RemovePane(previous.Id);
        var next = CreatePane(previous.Id, session.Ws, previous.StartCwd, previous.FontSize,
            profileName: session.ProfileName, liveBinding: JsonSerializer.SerializeToElement(binding with
            { TransportTicket = null, AttachmentNonce = null, Recovery = "pending", Error = null }));
        next.LiveClaim = claim;
        next.Appearance = previous.Appearance; next.FontZoomed = previous.FontZoomed;
        next.Ratio = previous.Ratio; next.ReadOnly = previous.ReadOnly; next.Overlay = previous.Overlay;
        next.Unread = previous.Unread; next.UnreadCategory = previous.UnreadCategory;
        lock (_workspaces)
        {
            if (ReferenceEquals(session.HiddenSplit, previous)) session.HiddenSplit = next;
            else session.Panes[session.Panes.IndexOf(previous)] = next;
        }
        if (ReferenceEquals(_selPane, previous)) { _selPane = null; _selecting = false; }
        if (ReferenceEquals(_active, session)) SyncSession();
        RegridSession(session); SaveState(); RequestRedraw();
        return next;
    }

    private async Task StartRemotePaneAsync(Pane pane, AgtermRemoteBinding binding)
    {
        if (!pane.LiveRecovery.TryBegin(out long generation)) return;
        bool failed = false;
        try
        {
            await _remoteOperations.RevalidateAsync(binding).ConfigureAwait(false);
            if (!pane.LiveRecovery.IsCurrent(generation)) return;
            string nonce = Guid.NewGuid().ToString("N");
            await LiveUiAsync(() =>
            {
                if (!pane.LiveRecovery.IsCurrent(generation)) return;
                ConfigureLiveTitle(pane, nonce);
                if (!SaveState(sync: true)) throw new IOException("Cannot save remote attachment checkpoint");
            }).ConfigureAwait(false);
            if (!pane.LiveRecovery.IsCurrent(generation)) return;
            string[] argv = AgtermRemoteOperations.AttachmentArgs(binding, nonce, pane.LiveClaim);
            if (pane.S is not ServerSession server) throw new IOException("Remote transport requires the Rust host");
            await server.StartCheckpointedAsync("wsl.exe", new SessionCommand("wsl.exe", argv).QuotedArgs,
                ticket => LiveUiAsync(() =>
                {
                    if (!pane.LiveRecovery.IsCurrent(generation)) throw new OperationCanceledException();
                    pane.LiveBinding = JsonSerializer.SerializeToElement(binding with
                        { TransportTicket = ticket, AttachmentNonce = nonce, Recovery = "pending", Error = null });
                    if (!SaveState(sync: true)) throw new IOException("Cannot save prepared remote transport ticket");
                }).GetAwaiter().GetResult()).ConfigureAwait(false);
            if (!pane.LiveRecovery.IsCurrent(generation)) { pane.S.Dispose(); return; }
            if (server.HasExited || string.IsNullOrEmpty(server.CreationTicket))
                throw new IOException("Remote transport did not start");
            bool acknowledged = false;
            for (int i = 0; i < 100 && !acknowledged; i++)
            {
                if (!pane.LiveRecovery.IsCurrent(generation)) { pane.S.Dispose(); return; }
                if (server.HasExited) throw new IOException("Remote client exited before attachment");
                await LiveUiAsync(() => acknowledged = pane.LiveRole?.Role is "leader" or "follower" or "unowned").ConfigureAwait(false);
                if (!acknowledged) await Task.Delay(100).ConfigureAwait(false);
            }
            if (!acknowledged) throw new IOException("Remote client did not acknowledge its managed role");
            await LiveUiAsync(() =>
            {
                if (!pane.LiveRecovery.IsCurrent(generation)) return;
                pane.LiveBinding = JsonSerializer.SerializeToElement(binding with { Recovery = "remote-reattached", Error = null,
                    TransportTicket = server.CreationTicket, AttachmentNonce = nonce });
                pane.LiveSaveError = null;
                if (!SaveState(sync: true)) throw new IOException("Cannot save remote transport ticket");
                _remoteRetries.Remove(binding.BindingId);
                RequestRedraw();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failed = true;
            await LiveUiAsync(() =>
            {
                if (!pane.LiveRecovery.IsCurrent(generation)) return;
                // A failed handshake must not leave an untracked local SSH attachment alive.
                pane.S.Dispose();
                pane.LiveBinding = JsonSerializer.SerializeToElement(binding with { Recovery = "unavailable", Error = ex.Message, TransportTicket = null });
                ShowLiveError(pane, ex.Message); SaveState();
            }).ConfigureAwait(false);
        }
        finally
        {
            pane.LiveRecovery.Complete(generation);
            if (failed && !pane.LiveClosing) _ = ReconnectRemotePaneAsync(pane);
        }
    }

    private async Task ReconnectRemotePaneAsync(Pane pane)
    {
        if (pane.Start is { } start) await start.ConfigureAwait(false);
        int attempt = 0;
        AgtermRemoteBinding? binding = null;
        await LiveUiAsync(() =>
        {
            if (pane.LiveClosing || !AllSessions().Any(s => s.AllPanes.Contains(pane))) return;
            var saved = AgtermRemoteBinding.Load(pane.LiveBinding, out _);
            if (saved is null || !_remoteReconnectPending.Add(pane)) return;
            binding = saved;
            attempt = _remoteRetries.GetValueOrDefault(binding.BindingId) + 1;
            _remoteRetries[binding.BindingId] = attempt;
        }).ConfigureAwait(false);
        if (binding is null) return;
        if (attempt > 5) { await LiveUiAsync(() => _remoteReconnectPending.Remove(pane)).ConfigureAwait(false); return; }
        await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1))).ConfigureAwait(false);
        await LiveUiAsync(() =>
        {
            _remoteReconnectPending.Remove(pane);
            if (pane.LiveClosing) return;
            var owner = AllSessions().FirstOrDefault(s => s.AllPanes.Contains(pane));
            if (owner is null || AgtermRemoteBinding.Load(pane.LiveBinding, out _)?.BindingId != binding.BindingId) return;
            ReplaceRemotePane(owner, pane, binding);
        }).ConfigureAwait(false);
    }

    private JsonElement? RemoteTerminalCommand(string command, JsonElement args, ISession session)
    {
        var binding = InvokeOnUiQueued(() =>
        {
            var pane = AllSessions().SelectMany(s => s.AllPanes).FirstOrDefault(p => ReferenceEquals(p.S, session));
            if (pane is null || !AgtermRemoteBinding.IsRemote(pane.LiveBinding)) return null;
            if (pane.LiveClosing || command == "session.type" && pane.ReadOnly) throw new InvalidOperationException("Remote pane is closing or readonly");
            var saved = AgtermRemoteBinding.Load(pane.LiveBinding, out var error) ?? throw new InvalidOperationException(error);
            if (saved.Recovery is not ("host-adopted" or "remote-reattached")) throw new InvalidOperationException("No verified remote attachment");
            return saved;
        });
        if (binding is null) return null;
        if (command == "session.type") return _remoteOperations.TypeAsync(binding,
            args.GetProperty("text").GetString()!.Replace("\r\n", "\r").Replace('\n', '\r')).GetAwaiter().GetResult();
        bool all = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("all", out var a) && a.ValueKind == JsonValueKind.True;
        int lines = 0;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("lines", out var l)) OverlayPanes.TryLines(l.GetRawText(), out lines);
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("styles", out var style) && style.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("Origin does not expose styled cell queries");
        var screen = _remoteOperations.ScreenAsync(binding, all || lines > 0).GetAwaiter().GetResult();
        if (command == "surface.cursor") return screen.GetProperty("cursorColumn").Clone();
        string text = screen.GetProperty("text").GetString() ?? "";
        if (lines > 0) text = string.Join('\n', text.TrimEnd('\n').Split('\n').TakeLast(lines));
        return JsonSerializer.SerializeToElement(text.TrimEnd('\n'));
    }

    private static JsonElement RemoteReadback(Pane pane)
    {
        var binding = AgtermRemoteBinding.Load(pane.LiveBinding, out var error);
        return JsonSerializer.SerializeToElement(new { provider = "agterm-ssh", bindingId = binding?.BindingId,
            transport = "wsl-ssh", distribution = binding?.Target.Distribution, user = binding?.Target.User,
            destination = binding?.Target.Destination,
            endpoint = binding?.Endpoint, originSession = binding?.OriginSession, originWindow = binding?.OriginWindow,
            originWorkspace = binding?.OriginWorkspace, originPane = binding?.Role, daemon = binding?.Daemon,
            ownership = "borrowed", recovery = binding?.Recovery ?? "unavailable", error = pane.LiveSaveError ?? binding?.Error ?? error,
            role = pane.LiveRole?.Role ?? "unknown", closeAction = "disconnect-attachment",
            remoteAttach = binding?.Recovery is "host-adopted" or "remote-reattached", inboundExport = false,
            presentation = false, terminate = false, restart = false });
    }
}
