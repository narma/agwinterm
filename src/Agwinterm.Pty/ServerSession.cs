using System.Diagnostics;
using Agwinterm.Core;
using Microsoft.Win32.SafeHandles;

namespace Agwinterm.Pty;

/// <summary>
/// The pty-host session backend (#105, Phase 2b): sessions live in the <c>--pty-host</c> process;
/// the UI holds <see cref="ServerSession"/> handles. Selected by <c>session-host = server</c>.
/// Spawns the host on demand (same exe, host role) and connects lazily — construction is cheap and
/// never touches the wire; the first <see cref="Create"/> does.
/// </summary>
public sealed class ServerSessionBackend : ISessionBackend, IDisposable
{
    private readonly string _appId;
    private readonly string? _exePath;
    private readonly string _spawnArgs;
    internal string SpawnArgs => _spawnArgs;
    internal string? ReplicaCore { get; }
    internal bool SingleOwner { get; }
    private readonly object _lock = new();
    private PtyHostClient? _client;
    private bool _disposed;
    private readonly StartupHostClaims _startupClaims;

    /// <param name="appId">Instance id — names the host's control pipe.</param>
    /// <param name="exePath">The host exe to spawn when none is running; null = require an
    /// already-running host (tests).</param>
    public ServerSessionBackend(string appId, string? exePath, string? spawnArgs = null)
        : this(appId, exePath, singleOwner: true, spawnArgs: spawnArgs, replicaCore: "ghostty") { }

    internal ServerSessionBackend(string appId, string? exePath, bool singleOwner, string? spawnArgs = null,
        string? replicaCore = null)
    {
        _appId = appId;
        _startupClaims = StartupHostClaims.ForNamespace(appId);
        _exePath = exePath;
        _spawnArgs = spawnArgs ?? $"--pty-host --pipe \"{appId}\"";
        ReplicaCore = replicaCore;
        SingleOwner = singleOwner;
    }

    public string Name => "server";

    public ISession Create(string id, int cols, int rows)
    {
        _startupClaims.Claim(id); // before connection, adoption, async spawn, or UI publication
        EnsureClient();   // connect/spawn NOW so an unreachable host fails here, where callers can fall back
        return new ServerSession(this, id, cols, rows);
    }

    /// <summary>Allocate a fresh local transport for an existing-only remote binding. Restarting
    /// an empty host here does not replay any other sessions or create an origin workload.</summary>
    public ISession CreateExistingOnlyTransport(string id, int cols, int rows)
    {
        _startupClaims.Claim(id);
        EnsureClient(allowEmptyHostRestart: true);
        return new ServerSession(this, id, cols, rows);
    }

    /// <summary>One best-effort sweep after restore. Every handle created in this host namespace is a
    /// claim, even before its pane is published. Closed/failed handles stay conservatively claimed
    /// for this sweep; their own exact lifecycle cleanup remains responsible for their child.</summary>
    public void ReapUnclaimedStartupSessions()
    {
        if (!_startupClaims.TryBeginSweep()) return;
        try
        {
            using var probe = ConnectExisting();
            foreach (var info in probe.List())
                try { _startupClaims.TryReap(info.Id, () => probe.Kill(info.Id, info.CreationTicket)); }
                catch { } // this exact attempt failed; never substitute another incarnation
        }
        catch { } // host unavailable: nothing can be proven safe to reap
        finally { _startupClaims.Complete(); }
    }

    internal PtyHostClient Client => EnsureClient();
    // Reconciliation must never start a new host or reuse an ambiguous request/reply stream.
    internal PtyHostClient ConnectExisting() => PtyHostClient.Connect(_appId);

    private PtyHostClient EnsureClient(bool allowEmptyHostRestart = false)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_client is not null)
            {
                if (_client.IsUsable) return _client;
                _client.Dispose();
                if (!allowEmptyHostRestart) return _client = ConnectExisting(); // no ordinary replay/spawn
                _client = null;
            }
            if (!PtyHostClient.IsRunning(_appId))
            {
                if (_exePath is null || !File.Exists(_exePath))
                    throw new InvalidOperationException("no pty-host is running and no exe to spawn one");
                var psi = new ProcessStartInfo(_exePath, _spawnArgs)
                { UseShellExecute = false, CreateNoWindow = true };
                Process.Start(psi);
                for (int i = 0; i < 50 && !PtyHostClient.IsRunning(_appId); i++) Thread.Sleep(100);
            }
            return _client = PtyHostClient.Connect(_appId);
        }
    }

    public void Dispose()
    {
        lock (_lock) { _disposed = true; _client?.Dispose(); _client = null; }
    }
}

/// <summary>
/// An <see cref="ISession"/> whose terminal lives in the pty-host process. The UI-side
/// <see cref="Emulator"/> is a REPLICA fed by the host's raw ConPTY stream (the host keeps the
/// authoritative one); input, resize, and lifecycle travel over the host protocol.
///
/// Lifecycle: <see cref="Dispose"/> KILLS the hosted session (explicit pane close);
/// <see cref="Detach"/> releases it alive (app quit) for a later <see cref="TryAdopt"/> — that
/// split is what lets shells survive UI restarts, updates, and crashes (#105, Phase 2c).
/// </summary>
public sealed partial class ServerSession : ISession
{
    public bool UsesPresentation => _backend.SingleOwner;
    public ISessionPresentation? Presentation { get; }
    private ulong _viewportRevision;
    private int _viewportScroll, _viewportCellWidth=8, _viewportCellHeight=18;
    private bool _viewportVisible=true;
    internal void AdvanceHiddenReadback()
    {
        // An explicit read is also a consumer of the cached viewport. A window
        // on a private desktop, hidden or occluded, may never complete a paint;
        // release this frame's credit so a subsequent read can see newer output.
        if(!UsesPresentation) return;
        ulong sequence;
        lock(_sync) { using var frame=((PresentedTerminalView)Emulator).AcquireFrame(); sequence=frame?.Identity.Sequence ?? 0; }
        if(sequence != 0) AcknowledgeFrame(sequence);
    }
    private PresentationInputSender? _presentationInput;
    private readonly Queue<TerminalInput> _startupInput=new();
    private int _startupInputBytes;
    private ulong _attachmentEpoch;
    private string _queryPipe = "";
    private string _effectPipe = "";
    private Task _effectReader=Task.CompletedTask;
    private ulong _presentedSequence;
    public void AcknowledgeFrame(ulong sequence)
    {
        lock(_sync)
        {
            if(sequence <= _presentedSequence || _inputClosed && !HasExited) return;
            if(SubmitInput(new TerminalInput.FramePresented(sequence)) == TerminalInputAcceptance.Accepted) _presentedSequence=sequence;
        }
    }
    private readonly TerminalLatencyTrace.MarkerReader? _latencyReceive = TerminalLatencyTrace.Enabled ? new() : null;
    private readonly object _sync = new();
    private readonly ServerSessionBackend _backend;
    private readonly string _id;   // = the pane id; names the hosted session (the adoption key)
    private Stream? _data;
    private readonly CancellationTokenSource _readCancel = new();   // unblocks ReadLoop's pending read (#118)
    private int? _childPid;
    private volatile bool _started;
    private readonly SessionStartFence _startFence = new();
    private volatile bool _disposed;
    private volatile bool _stopped;
    public bool Stopped => _stopped;
    private volatile string _creationTicket = "";
    private uint _creationHostPid;

    public ITerminalCore Emulator { get; private set; }
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int? ChildProcessId => _childPid;
    public object SyncRoot => _sync;
    public event Action? OutputReceived;

    public int? ExitCode { get; private set; }
    public bool HasExited { get; private set; }
    private volatile bool _inputClosed;
    private volatile bool _childInputClosed;
    public bool InputClosed => _inputClosed || _childInputClosed || _disposed;
    internal void MarkInputClosed() => _inputClosed = true;
    public event Action<int>? Exited;

    internal ServerSession(ServerSessionBackend backend, string id, int cols, int rows)
    {
        _backend = backend;
        _id = id;
        Cols = cols;
        Rows = rows;
        Emulator = backend.SingleOwner ? new PresentedTerminalView(cols,rows) : TerminalSession.CreateCore(cols, rows, backend.ReplicaCore);
        if(backend.SingleOwner) Presentation=new RemoteSessionPresentation(this);
        if (Emulator is GhosttyTerminalCore ghostty) ghostty.MarkLegacyReplica();
    }

    /// <summary>True when this handle reconnected to a SURVIVING hosted session instead of spawning
    /// a fresh shell — restore must then skip buffer seeding, agent-resume typing, and
    /// restore-commands (the real thing is still running; typing a resume into it would be chaos).</summary>
    public bool Adopted { get; private set; }
    public string CreationTicket => _creationTicket;

    // ---- Agent status: a UI-process concept (set via the control API), tracked locally exactly
    // like TerminalSession — the host knows nothing about it. Kept duplicated (25 lines) rather
    // than shared so the two implementations stay independently obvious.
    public StatusSignal StatusSignal { get; } = new();
    public AgentStatus Status => StatusSignal.Snapshot.Status;
    public bool Blink => StatusSignal.Snapshot.Blink;
    public bool AutoReset => StatusSignal.Snapshot.AutoReset;
    public event Action? StatusChanged;
    public event Action<string?>? SoundRequested;

    /// <summary>Epoch seconds of the last status WRITE (see TerminalSession for why every write,
    /// not every change). Seeded at construction.</summary>
    public long StatusChangedAt => StatusSignal.Snapshot.ChangedAt;

    /// <summary>Test seam: back-date the status stamp, so a re-assert's re-stamp is observable
    /// without sleeping out a whole epoch second on every run.</summary>
    internal void BackdateStatus(long epochSeconds) => StatusSignal.Backdate(epochSeconds);

    public void SetStatus(AgentStatus status, bool blink = false, bool autoReset = false,
        bool sound = false, string? soundName = null)
    {
        bool changed = StatusSignal.Set(status, blink, autoReset);
        if (sound && status != AgentStatus.Idle)
            try { SoundRequested?.Invoke(soundName); } catch { }
        if (changed) StatusChanged?.Invoke();
    }

    public void NotifyActivity()
    {
        if (Status is AgentStatus.Blocked or AgentStatus.Completed)
            SetStatus(AgentStatus.Idle);
    }

    // ---- Lifecycle ----

    public Task StartAsync(string app, string[] commandLine, bool verbatimCommandLine = false,
        IReadOnlyDictionary<string, string>? extraEnv = null, string? cwd = null, bool deElevate = false,
        bool freshEnv = true, CancellationToken ct = default)
        => StartCoreAsync(app, commandLine, verbatimCommandLine, extraEnv, cwd, deElevate, freshEnv, ct, null);

    /// <summary>Persist an exact transport ticket before the host may spawn its child.</summary>
    public Task StartCheckpointedAsync(string app, string[] commandLine, Action<string> persistTicket)
        => StartCoreAsync(app, commandLine, true, null, null, false, true, default, persistTicket);

    private async Task StartCoreAsync(string app, string[] commandLine, bool verbatimCommandLine,
        IReadOnlyDictionary<string, string>? extraEnv, string? cwd, bool deElevate,
        bool freshEnv, CancellationToken ct, Action<string>? persistTicket)
    {
        try
        {
            await Task.Run(() =>
            {
                if (_startFence.IsClosed) return;
                var client = _backend.Client;
                if (persistTicket is not null && client.CreationRevision < 1)
                    throw new IOException("Checkpointed transport requires creation tickets");
                if (client.CreationRevision >= 1)
                {
                    // A lost preparation reply owns no child. Once this ticket is known, close
                    // can cancel even while the following create is still blocked on its reply.
                    _creationHostPid = client.HostPid;
                    _creationTicket = client.PrepareCreate(_id);
                    if (_startFence.Created()) KillHosted();
                    if (_startFence.IsClosed) return;
                }
                try
                {
                    persistTicket?.Invoke(_creationTicket);
                    if (_startFence.IsClosed) return;
                    int cols = Cols, rows = Rows;
                    client.Create(_id, cols, rows, app, commandLine, cwd, extraEnv,
                        verbatim: verbatimCommandLine, deElevate: deElevate, freshEnv: freshEnv, creationTicket: _creationTicket,
                        emulatorCore: UsesPresentation ? "ghostty" : TerminalSession.CoreName(Emulator), singleOwner: UsesPresentation);
                    if (_startFence.Created() || _startFence.KillRequested) KillHosted(); // retry exact cancellation if close raced a reply
                    if (_startFence.IsClosed) return;
                    // Even the first attachment can follow output from a fast child.
                    // Ghostty transfers a native snapshot; legacy cores need a viewport repaint.
                    if (PublishAttachment(client.Attach(_id, repaint: true, creationTicket: _creationTicket, nativeSnapshot: true, presentationRevision: UsesPresentation ? 1u : 0)) && (Cols != cols || Rows != rows))
                        Resize(Cols, Rows);
                }
                catch
                {
                    if (_startFence.AttachmentFailed() || (_creationTicket.Length > 0 && _startFence.KillRequested)) KillHosted();
                    throw;
                }
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            // Same shape as TerminalSession's de-elevate failure: surface it IN the pane instead of
            // leaving a dead surface (or crashing an unobserved fire-and-forget task).
            var msg = $"\r\n\x1b[31m[agwinterm] pty-host session failed to start:\x1b[0m\r\n  {ex.Message}\r\n";
            lock (_sync)
            {
                if (!_disposed)
                {
                    if(Emulator is PresentedTerminalView view) view.ShowError("PTY host session failed to start:\n"+ex.Message);
                    else Emulator.Feed(System.Text.Encoding.UTF8.GetBytes(msg));
                }
            }
            OutputReceived?.Invoke();
            MarkInputClosed(); ExitCode = 1; HasExited = true;
            return;
        }
    }

    private bool PublishAttachment(PtyHostAttachment att)
    {
        lock (_sync)
        {
            if (_disposed) { att.Dispose(); return false; }
            try
            {
                if (UsesPresentation)
                {
                    if (att.PresentationRevision != 1 || att.Input is null || att.AttachmentEpoch == 0 ||
                        string.IsNullOrEmpty(att.QueryPipe) || string.IsNullOrEmpty(att.EffectPipe))
                        throw new IOException("Missing negotiated presentation transport.");
                    _attachmentEpoch = att.AttachmentEpoch;
                    _queryPipe = att.QueryPipe;
                    _effectPipe = att.EffectPipe;
                }
                else
                {
                PrepareReplica(att);
                if (att.NativeSnapshot is { } snapshot)
                {
                    if (Emulator is not GhosttyTerminalCore native) throw new IOException("Native snapshot requires Ghostty.");
                    native.RestoreSnapshot(snapshot);
                }
                else
                {
                    if (att.ScrollbackBlob is { Length: > 0 } blob
                        && BufferPersist.TryParse(blob, out var pbuf))
                        BufferPersist.Restore(Emulator, pbuf);
                    else
                        Emulator.SeedScrollback(att.Scrollback);
                    Emulator.Feed(System.Text.Encoding.UTF8.GetBytes(att.Modes));
                }
                }
            }
            catch { att.Dispose(); throw; }
        }
        bool published = _startFence.Publish(() =>
        {
            _data = att.Data; _childPid = att.ChildPid;
            if (UsesPresentation)
            {
                lock(_sync)
                {
                    _presentationInput = new(att.Input!,att.CreationTicket,att.AttachmentEpoch);
                    while(_startupInput.TryDequeue(out var input))
                        if(!InputClosed && _presentationInput.Submit(input) != TerminalInputAcceptance.Accepted) MarkInputClosed();
                    _startupInputBytes=0;
                }
            }
            if (UsesPresentation && _effectPipe.Length > 0) _effectReader=ReadEffectsAsync();
            _started = true; // from here the reader alone owns disposal, even if close cancels it now
            _ = Task.Factory.StartNew(ReadLoop, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        });
        if (!published) att.Dispose(); // no read was ever issued
        if (published && Emulator is GhosttyTerminalCore restored && (restored.Cols != Cols || restored.Rows != Rows)) Resize(Cols, Rows);
        if (published)
        {
            if(UsesPresentation) _presentationReady.TrySetResult();
            if(UsesPresentation) ExecutePresentation(new TerminalCommand.ScrollbackLimit(Emulator.ScrollbackMax));
            UpdateTheme();
        }
        return published;
    }

    // Attachment negotiates authoritative state before any output is consumed. The
    // constructor's provisional core reflects the current UI preference, which may
    // differ from a surviving shell. Never replace a live, already-started replica.
    private void PrepareReplica(PtyHostAttachment attachment)
    {
        string core = attachment.NativeSnapshot is not null ? "ghostty" : attachment.EmulatorCore;
        if (core.Length == 0 || core == TerminalSession.CoreName(Emulator)) return;
        if (_started) throw new InvalidOperationException("Cannot switch a live terminal replica's core");
        var replacement = TerminalSession.CreateCore(Cols, Rows, core);
        try
        {
            if (replacement is GhosttyTerminalCore ghostty) ghostty.MarkLegacyReplica();
            replacement.Host = Emulator.Host;
            replacement.ScrollbackMax = Emulator.ScrollbackMax;
            replacement.CellPixelWidth = Emulator.CellPixelWidth;
            replacement.CellPixelHeight = Emulator.CellPixelHeight;
        }
        catch { (replacement as IDisposable)?.Dispose(); throw; }
        var previous = Emulator;
        Emulator = replacement;
        (previous as IDisposable)?.Dispose();
    }

    private void KillHosted()
    {
        if (_creationTicket.Length == 0)
        {
            // Legacy peers cannot disambiguate a lost create reply. Preserve their established
            // post-ack close behavior, but never pretend it has ticket-based recovery.
            try { using var client = _backend.ConnectExisting(); client.Kill(_id); } catch { }
            return;
        }
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var client = _backend.ConnectExisting();
                if (_creationHostPid == 0 || client.HostPid != _creationHostPid) continue;
                var state = client.CancelCreate(_id, _creationTicket);
                if (state == Proto.CreationPhase.CreationUnknown) return; // exact attempt proven absent
                if (state == Proto.CreationPhase.CreationCancelling)
                {
                    Trace.WriteLine($"pty-host creation {_creationTicket}: cancellation accepted, cleanup pending");
                    return; // host creator retains the disposal obligation; this is not a completion claim
                }
            }
            catch (Exception ex) { Trace.WriteLine($"pty-host exact cancellation attempt failed: {ex.Message}"); }
        }
        Trace.WriteLine($"pty-host creation {_creationTicket}: cleanup remains unproven");
    }

    public async Task<int> RunAsync(string app, string[] commandLine, bool verbatimCommandLine = false, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exited += code => tcs.TrySetResult(code);                    // subscribe BEFORE start: no exit race
        await StartAsync(app, commandLine, verbatimCommandLine, ct: ct).ConfigureAwait(false);
        if (HasExited) return ExitCode ?? 0;                         // failed to start / exited during attach
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Adopt a surviving hosted session with this pane's id (after a UI restart or crash):
    /// attach with a repaint, seed the replica from the host's snapshot (scrollback text + modes),
    /// and go live — the child process keeps running untouched. False = nothing to adopt, launch
    /// fresh; an exited leftover is reaped here so the fresh launch starts clean.</summary>
    public bool TryAdopt(string? expectedCreationTicket = null, bool cancelUnattached = false)
    {
        if (_startFence.IsClosed) return false;
        PtyHostAttachment att;
        try { var client = _backend.Client; att = client.Attach(_id, repaint: true, creationTicket: expectedCreationTicket ?? "", nativeSnapshot: true, presentationRevision: UsesPresentation ? 1u : 0); _creationHostPid = client.HostPid; }
        catch
        {
            // Recovery may find a checkpoint written before Create reached publication.
            // Cancel only its exact reservation/transport, including an in-flight create.
            if (cancelUnattached && expectedCreationTicket is { Length: > 0 })
                _backend.Client.CancelCreate(_id, expectedCreationTicket);
            return false;
        }
        _creationTicket = att.CreationTicket;
        if (att.HasExited)
        {
            att.Dispose();
            KillHosted();
            return false;
        }
        lock (_sync)
        {
            if (_disposed) { att.Dispose(); return false; }
        }
        if (_startFence.Created()) KillHosted();
        if (!PublishAttachment(att)) return false;
        Adopted = true;
        // The new UI already reports its desired grid, so its ordinary layout guard will not
        // detect an old host grid. The reader must be live before resizing (ConPTY may repaint).
        if (att.Cols != Cols || att.Rows != Rows) Resize(Cols, Rows);
        OutputReceived?.Invoke();
        return true;
    }

    /// <summary>Default-terminal handoff hands us raw HANDLES — they only mean something in this
    /// process. Handoff panes are pinned to the in-process backend at the creation site.</summary>
    public void Attach(SafeFileHandle conOut, SafeFileHandle conIn, SafeFileHandle signal, IntPtr clientProcess, int pid)
        => throw new NotSupportedException("default-terminal handoff sessions run in-process");

    /// <summary>Mirror of TerminalSession's pump, fed by the host's data pipe instead of ConPTY.
    /// OWNS the pipe (issue #118): Detach/Dispose only CANCEL the pending read — this loop is the
    /// one place the pipe is disposed, strictly after that read has completed, so no overlapped
    /// operation is ever in flight at close (that race is a native AV in the IOCP poller).</summary>
    private void ReadLoop()
    {
        if (UsesPresentation) { ReadPresentationLoop(); return; }
        var data = _data!;
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                // Sync-over-async on a dedicated thread (the pipe handle is async so cancellation
                // can unblock us); same read cadence as before, now with a cancel seam.
                int n = data.ReadAsync(buffer, 0, buffer.Length, _readCancel.Token).GetAwaiter().GetResult();
                if (n <= 0) break;
                _latencyReceive?.Output(buffer.AsSpan(0, n), 25);
                lock (_sync) { if (_disposed) break; Emulator.Feed(buffer.AsSpan(0, n)); }
                OutputReceived?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        MarkInputClosed(); // EOF also means detached/superseded; that need not be a child exit.
        try { data.Dispose(); } catch { }                            // safe now: our read is done
        OnStreamEnded();
    }

    /// <summary>Data-pipe EOF: the child exited, the session was killed, or the host died — ask the
    /// host which (the exit code travels via <c>list</c>). A locally-initiated dispose is none of
    /// these. An EOF for a session the host says is still alive is a supersede/detach: not an exit.</summary>
    private void OnStreamEnded()
    {
        if (_disposed || _stopped || HasExited) return;
        int? code = null;
        try
        {
            var info = _backend.Client.List().FirstOrDefault(i => i.Id == _id &&
                (_creationTicket.Length == 0 || i.CreationTicket == _creationTicket));
            if (info is not null && !info.HasExited) return;         // detached, still running (2c territory)
            code = info?.ExitCode;
        }
        catch { }                                                    // host gone → exited, code unknown
        if (_disposed || _stopped) return;
        ExitCode = code;
        HasExited = true;
        try { Exited?.Invoke(code ?? 0); } catch { }
    }

    // ---- I/O ----

    public void Inject(ReadOnlySpan<byte> bytes)
    {
        if(UsesPresentation)
        {
            if(!_started) throw new InvalidOperationException("Terminal owner is not attached.");
            while(!bytes.IsEmpty)
            {
                int count=Math.Min(bytes.Length,64*1024);
                if(SubmitInput(new TerminalInput.Command(new TerminalCommand.InjectOutput(bytes[..count].ToArray()))) != TerminalInputAcceptance.Accepted)
                    throw new IOException("Owner display injection was not accepted.");
                bytes=bytes[count..];
            }
            return;
        }
        lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); Emulator.Feed(bytes); }
        OutputReceived?.Invoke();
    }

    public void MutateLocked(Action<ITerminalCore> mutate)
    {
        lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); mutate(Emulator); }
        OutputReceived?.Invoke();
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (UsesPresentation)
        {
            var acceptance = SubmitInput(new TerminalInput.Raw(bytes.ToArray()));
            if (acceptance != TerminalInputAcceptance.Accepted) throw new IOException("Presentation input was not accepted: "+acceptance);
            return;
        }
        if (InputClosed) throw new IOException("Session input is closed.");
        var data = _data ?? throw new InvalidOperationException("Session not started.");
        try { data.Write(bytes); data.Flush(); TerminalLatencyTrace.Record(22, bytes.Length); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        { MarkInputClosed(); throw; }
    }

    public void Resize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0) return;
        if(UsesPresentation)
        {
            lock(_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed,this);
                if((long)cols*rows > TerminalFrame.MaximumCells || cols > 10000 || rows > 10000) throw new ArgumentOutOfRangeException(nameof(cols));
                if(Cols == cols && Rows == rows) return;
                Cols=cols; Rows=rows;
                if(_started) SendViewportLocked();
            }
            return;
        }
        lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); Emulator.Resize(cols, rows); }
        Cols = cols;
        Rows = rows;
        if (_started && !HasExited)
            try { _backend.Client.Resize(_id, cols, rows, _creationTicket); } catch { }   // host gone → EOF path reports it
    }

    private readonly object _themeGate = new();
    private long _themeSequence;
    /// <summary>Coalesce live theme changes off the UI thread. Never replay VT or resize a child.</summary>
    public void UpdateTheme()
    {
        string colors, palette, cursor, ticket;
        uint cursorDefault = 0;
        long sequence;
        lock (_sync)
        {
            if (_disposed || !_started || HasExited || Emulator is not (GhosttyTerminalCore or PresentedTerminalView)) return;
            var host = Emulator.Host;
            if (host?.DefaultColors is not { } defaults || host.DefaultPalette is not { } entries || host.DefaultCursorColor is not { } caret) return;
            colors = ThemeColors.Format(defaults.Foreground, defaults.Background);
            palette = ThemeColors.FormatPalette(entries); cursor = ThemeColors.FormatColor(caret);
            if (host.DefaultCursor is { } shape)
            {
                cursorDefault = shape.Style switch { CursorStyle.Block => 1u, CursorStyle.Underline => 3u, _ => 5u };
                if (!shape.Blink) cursorDefault++;
            }
            ticket = _creationTicket;
            sequence = Interlocked.Increment(ref _themeSequence);
        }
        _ = Task.Run(() =>
        {
            lock (_themeGate)
            {
                if (_disposed || sequence != Volatile.Read(ref _themeSequence)) return;
                try { _backend.Client.UpdateTheme(_id, colors, palette, cursor, ticket, cursorDefault); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or TimeoutException) { }
            }
        });
    }

    public string SnapshotRow(int row)
    {
        lock (_sync) return Emulator.DumpRow(row);
    }

    /// <summary>Thread-safe snapshot of the caret's grid position, read from the local replica
    /// emulator — no pty-host round trip, so it can lag the host by the data pipe's latency.</summary>
    public (int Row, int Col) SnapshotCursor()
    {
        lock (_sync) return (Emulator.CursorRow, Emulator.CursorCol);
    }

    /// <summary>Stop viewing WITHOUT killing: cancel the read loop, which closes the data pipe (the
    /// host sees a detach) and leaves the child running for a later <see cref="TryAdopt"/>. The
    /// app-quit path. Never disposes the pipe directly — see ReadLoop's ownership rule (#118).</summary>
    public void Detach()
    {
        _startFence.Close(kill: false);
        DisposeReplica();                                           // EOF now means "we left", not "it died"
        CancelRead();
    }

    public void Dispose()
    {
        DisposeReplica();
        Stop();
    }

    /// <summary>Close the transport while retaining the replica for a visible terminal/error surface.</summary>
    public void Stop()
    {
        _stopped = true;
        HasExited = true;
        MarkInputClosed();
        if (_startFence.Close(kill: true)) KillHosted();
        CancelRead();
    }

    private void CancelRead()
    {
        lock(_sync) { _startupInput.Clear(); _startupInputBytes=0; }
        _presentationInput?.Dispose();
        try { _readCancel.Cancel(); } catch (ObjectDisposedException) { }
        // PublishAttachment either assigns the pipe to a reader, or closes the unpublished pipe.
        // Never inspect _started here to guess whether a concurrent publisher has issued a read.
    }

    public TerminalInputAcceptance SubmitInput(TerminalInput input,bool persistentUiThread=false)
    {
        lock(_sync)
        {
            if (_disposed || _inputClosed && !HasExited) return TerminalInputAcceptance.Closed;
            if (InputClosed && input is not (TerminalInput.Viewport or TerminalInput.FramePresented or TerminalInput.Command))
                return TerminalInputAcceptance.Closed;
            if(_presentationInput is null)
            {
                // Window focus and typing can precede the asynchronous attachment.
                // Retain a bounded, owned copy; never resend after an attachment fails.
                if (input is TerminalInput.Paste paste && !OwnerInputQueue.PasteFits(paste.Value)) return TerminalInputAcceptance.Rejected;
                int bytes;
                try { bytes=PresentationInputCodec.Encode(input,"00000000000000000000000000000000",ulong.MaxValue,ulong.MaxValue).Length; }
                catch(InvalidDataException) { MarkInputClosed(); return TerminalInputAcceptance.Overflow; }
                if(_startupInput.Count >= 4096 || bytes > OwnerInputQueue.MaximumBytes-_startupInputBytes)
                { if (input is TerminalInput.Paste) return TerminalInputAcceptance.Rejected;
                  _startupInput.Clear(); _startupInputBytes=0; MarkInputClosed(); return TerminalInputAcceptance.Overflow; }
                input=input switch {
                    TerminalInput.Raw raw=>new TerminalInput.Raw(raw.Bytes.ToArray()),
                    TerminalInput.Command { Value:TerminalCommand.InjectOutput output }=>new TerminalInput.Command(new TerminalCommand.InjectOutput(output.Bytes.ToArray())),
                    _=>input
                };
                _startupInput.Enqueue(input); _startupInputBytes+=bytes;
                return TerminalInputAcceptance.Accepted;
            }
            var result = _presentationInput.Submit(input,persistentUiThread);
            if (result is not (TerminalInputAcceptance.Accepted or TerminalInputAcceptance.Rejected)) MarkInputClosed();
            return result;
        }
    }
    private TerminalInputAcceptance SendViewportLocked()
        => SubmitInput(new TerminalInput.Viewport(++_viewportRevision,Cols,Rows,_viewportCellWidth,_viewportCellHeight,_viewportScroll,_viewportVisible));
    internal TerminalFrame.Lease? CapturePresentation(int scrollOffset,int cellWidth,int cellHeight)
    {
        if(scrollOffset < 0 || cellWidth is < 1 or > 65535 || cellHeight is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(scrollOffset));
        lock(_sync)
        {
            if(_disposed) return null;
            bool changed=_viewportScroll != scrollOffset || _viewportCellWidth != cellWidth || _viewportCellHeight != cellHeight || !_viewportVisible;
            _viewportScroll=scrollOffset; _viewportCellWidth=cellWidth; _viewportCellHeight=cellHeight; _viewportVisible=true;
            if(_started && (changed || _viewportRevision == 0)) SendViewportLocked();
            return ((PresentedTerminalView)Emulator).AcquireFrame();
        }
    }
    internal TerminalInputAcceptance ExecutePresentation(TerminalCommand command)
    {
        lock(_sync)
        {
            if(_disposed || _inputClosed && !HasExited) return TerminalInputAcceptance.Closed;
            switch(command)
            {
                case TerminalCommand.ScrollbackLimit limit when !_started:
                    Emulator.ScrollbackMax=limit.Rows;
                    return TerminalInputAcceptance.Accepted;
                case TerminalCommand.InjectOutput or TerminalCommand.Clear or TerminalCommand.Reset or TerminalCommand.ScrollbackLimit:
                    return SubmitInput(new TerminalInput.Command(command));
                case TerminalCommand.Resize resize:
                    if(resize.ViewportRevision < _viewportRevision || resize.Columns is < 1 or > 10000 || resize.Rows is < 1 or > 10000 ||
                        (long)resize.Columns*resize.Rows > TerminalFrame.MaximumCells || resize.CellWidth is < 1 or > 65535 || resize.CellHeight is < 1 or > 65535)
                        throw new ArgumentOutOfRangeException(nameof(command));
                    Cols=resize.Columns; Rows=resize.Rows; _viewportCellWidth=resize.CellWidth; _viewportCellHeight=resize.CellHeight;
                    _viewportRevision=Math.Max(_viewportRevision,resize.ViewportRevision);
                    break;
                case TerminalCommand.Viewport viewport:
                    if(viewport.ViewportRevision < _viewportRevision || viewport.ScrollOffset < 0) throw new ArgumentOutOfRangeException(nameof(command));
                    _viewportScroll=viewport.ScrollOffset; _viewportVisible=viewport.Visible;
                    _viewportRevision=Math.Max(_viewportRevision,viewport.ViewportRevision);
                    break;
                default: throw new NotSupportedException("Remote owner command transport is not implemented yet.");
            }
            return SendViewportLocked();
        }
    }
    private void ReadPresentationLoop()
    {
        var data = _data!;
        PresentationPipeIo? pipeReader = data is System.IO.Pipes.PipeStream pipe ? new(pipe, _readCancel.Token) : null;
        var reader = new PresentationFrameReader(data, pipeReader is null ? null : pipeReader.Read);
        var framePool = System.Buffers.ArrayPool<Cell>.Create(TerminalFrame.MaximumCells,3);
        TerminalFrame.Lease? previous = null;
        try
        {
            while (true)
            {
                var decoded = reader.Read(previous,_creationTicket,_attachmentEpoch,_readCancel.Token,framePool);
                using var wireFrame = decoded.Frame;
                using var frame = wireFrame.Images.Count == 0 ? null : ResolveFrameImagesAsync(wireFrame,_readCancel.Token).GetAwaiter().GetResult();
                var completeFrame = frame ?? wireFrame;
                if(decoded.Exited && !HasExited) awaitFinalCheckpoint();
                bool stale;
                lock (_sync)
                {
                    if (_disposed) break;
                    stale=!decoded.Exited && completeFrame.Identity.ViewportRevision < _viewportRevision;
                    if(!stale) ((PresentedTerminalView)Emulator).Publish(completeFrame);
                }
                previous?.Dispose(); previous=completeFrame.Acquire();
                if (wireFrame.Images.Count == 0) _presentationImages.Clear();
                if(stale || _childInputClosed) AcknowledgeFrame(completeFrame.Identity.Sequence);
                if(!stale) OutputReceived?.Invoke();
                if (decoded.Exited && !HasExited)
                {
                    _effectReader.GetAwaiter().GetResult();
                    ExitCode=decoded.ExitCode; HasExited=true; MarkInputClosed();
                    AcknowledgeFrame(completeFrame.Identity.Sequence);
                    Exited?.Invoke(decoded.ExitCode);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or Google.Protobuf.InvalidProtocolBufferException)
        { if(ex is not OperationCanceledException) Trace.WriteLine($"Presentation reader ended: {ex}"); }
        finally
        {
            previous?.Dispose(); _presentationImages.Clear(); MarkInputClosed(); _presentationInput?.Dispose();
            _readCancel.Cancel();
            try { _effectReader.GetAwaiter().GetResult(); }
            catch(Exception ex) when(ex is IOException or OperationCanceledException or Google.Protobuf.InvalidProtocolBufferException) { }
            pipeReader?.Dispose(); data.Dispose();
        }
        OnStreamEnded();
        void awaitFinalCheckpoint()
        {
            try { ExportBufferAsync(_readCancel.Token).GetAwaiter().GetResult(); }
            catch(Exception ex) when(ex is IOException or OperationCanceledException or TimeoutException)
            { Trace.WriteLine("Final terminal checkpoint failed: "+ex.Message); }
        }
    }
    private void DisposeReplica()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            (Emulator as IDisposable)?.Dispose();
        }
    }
}
