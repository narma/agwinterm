using System.Text.Json;
using System.Text;
using System.Security.Cryptography;

namespace Agwinterm.Pty;

/// <summary>Typed identities above the unchanged Rust host protocol.</summary>
public sealed class WslLiveOperations
{
    private readonly WslLiveClient _client = new();

    public async Task<WslLiveBinding> CreateAsync(ShellProfile profile, string appId, string paneId,
        string? cwd, IReadOnlyDictionary<string, string> environment,
        Func<WslLiveBinding, Task> persistReservation, CancellationToken ct = default)
    {
        var error = WslLiveProfiles.Validate(profile, "server-rust", WslLiveSurface.Ordinary);
        if (error is not null || profile.Zmx is not { Enabled: true })
            throw new InvalidOperationException(error ?? "Live creation requires explicit opt-in");
        var location = await _client.PrepareAsync(profile, appId, cwd, ct).ConfigureAwait(false);
        var endpoint = new WslLiveEndpoint(Guid.NewGuid().ToString(), location.Distribution, location.User,
            profile.Zmx.Executable!, location.SocketDirectory, appId, LedgerDirectory: location.Ledger, HelperPath: location.Helper);
        var binding = new WslLiveBinding(1, "wsl-zmx", Guid.NewGuid().ToString(), paneId, endpoint,
            null, "owned", "uncertain", Pending: true);
        // Disk must know the operation before the first potentially mutating request.
        await persistReservation(binding).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var env = WslLiveProfiles.WorkloadEnvironment(profile.Env);
        foreach (var item in WslLiveProfiles.WorkloadEnvironment(environment)) env[item.Key] = item.Value;
        var result = await _client.InvokeAsync(location, new { verb = "create", ledger = location.Ledger,
            operation = binding.BindingId, specification = new { executable = endpoint.Executable,
                socketDirectory = endpoint.SocketDirectory, cwd = location.Cwd,
                argv = WslLiveProfiles.WorkloadArgv(profile), environment = env } }, ct).ConfigureAwait(false);
        return Apply(binding, result, "fresh-started");
    }

    public async Task<WslLiveBinding> ReconcileAsync(WslLiveBinding binding, CancellationToken ct = default)
    {
        var location = Location(binding);
        if (binding.RestartOrigin is { } origin)
        {
            var previous = binding with { BindingId = origin.BindingId, Identity = origin.Identity,
                Pending = false, RestartOrigin = null, TransportTicket = null, AttachmentNonce = null };
            var reconciled = await _client.InvokeAsync(location, new { verb = "reconcile-restart",
                ledger = location.Ledger, operation = previous.BindingId, newOperation = binding.BindingId,
                identity = WireIdentity(previous) }, ct).ConfigureAwait(false);
            return Apply(reconciled.TryGetProperty("rollback", out var rollback) && rollback.GetBoolean()
                ? previous : binding, reconciled, "zmx-reattached");
        }
        var result = await _client.InvokeAsync(location,
            new { verb = "reconcile", ledger = location.Ledger, operation = binding.BindingId }, ct).ConfigureAwait(false);
        return Apply(binding, result, "zmx-reattached");
    }

    public async Task<string> TerminateAsync(WslLiveBinding binding, CancellationToken ct = default)
    {
        if (binding.Ownership != "owned" || binding.Identity is null)
            throw new InvalidOperationException("Exact owned identity is required for termination");
        var location = Location(binding);
        var result = await _client.InvokeAsync(location, new { verb = "terminate", ledger = location.Ledger,
            operation = binding.BindingId, identity = WireIdentity(binding) }, ct).ConfigureAwait(false);
        return result.GetProperty("state").GetString() ?? "uncertain";
    }

    public async Task<WslLiveBinding> RestartAsync(WslLiveBinding previous, string source, string? command,
        Func<WslLiveBinding, Task> persistReservation, CancellationToken ct = default)
    {
        if (previous.Ownership != "owned" || previous.Identity is null || previous.Recovery != "missing")
            throw new InvalidOperationException("Fresh start requires confirmed loss of an owned workload");
        if (source is not ("shell" or "agent" or "pin" or "captured") || source != "shell" && string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Selected fresh-start source is unavailable");
        // Checkpoints may point at an older content-addressed application helper.
        // Install this application's helper without replacing dependencies or
        // retargeting the recorded distribution/user/ledger/socket endpoint.
        var location = await _client.RefreshAsync(Location(previous), ct).ConfigureAwait(false);
        previous = previous with { Endpoint = previous.Endpoint with { HelperPath = location.Helper } };
        var pending = previous with { BindingId = Guid.NewGuid().ToString(), Identity = null, Pending = true,
            Recovery = "uncertain", TransportTicket = null, AttachmentNonce = null, Error = null,
            RestartOrigin = new(previous.BindingId, previous.Identity) };
        await persistReservation(pending).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var result = await _client.InvokeAsync(Location(previous), new { verb = "restart",
            ledger = previous.Endpoint.LedgerDirectory, operation = previous.BindingId, newOperation = pending.BindingId,
            identity = WireIdentity(previous), source, command }, ct).ConfigureAwait(false);
        return Apply(pending, result, "fresh-started");
    }

    public static string[] AttachmentArgs(WslLiveBinding binding, string nonce, bool claim = false) =>
        WslLiveClient.AttachmentArgs(Location(binding), new { verb = "attach", executable = binding.Endpoint.Executable,
            socketDirectory = binding.Endpoint.SocketDirectory, name = binding.Identity?.Name ?? throw new InvalidOperationException("No live identity"),
            identity = WireIdentity(binding), nonce, claim });

    public sealed record Discovered(string Daemon, string Incarnation, string Ownership, ulong Attachments,
        [property: System.Text.Json.Serialization.JsonIgnore] WslLiveBinding Binding);

    public async Task<IReadOnlyList<Discovered>> ListAsync(ShellProfile profile, string appId, CancellationToken ct = default)
    {
        if (!WslIntegration.IsWsl(profile.Command) || !WslLiveValidation.LinuxPath(profile.Zmx?.Executable))
            throw new InvalidOperationException("Profile must configure a Linux ZMX executable");
        var location = await _client.PrepareAsync(profile, appId, null, ct).ConfigureAwait(false);
        var result = await _client.InvokeAsync(location, new { verb = "inventory", executable = profile.Zmx!.Executable,
            socketDirectory = location.SocketDirectory, ledger = location.Ledger }, ct).ConfigureAwait(false);
        var endpoint = new WslLiveEndpoint(Guid.NewGuid().ToString(), location.Distribution, location.User,
            profile.Zmx!.Executable!, location.SocketDirectory, appId, LedgerDirectory: location.Ledger, HelperPath: location.Helper);
        var sessions = new List<Discovered>();
        foreach (var row in result.GetProperty("sessions").EnumerateArray())
        {
            string? operation = row.GetProperty("operation").GetString();
            var binding = new WslLiveBinding(1, "wsl-zmx", operation ?? Guid.NewGuid().ToString(),
                row.GetProperty("originPane").GetString() ?? row.GetProperty("name").GetString()!, endpoint,
                null, operation is null ? "external" : "owned", "uncertain", Pending: true);
            var ready = JsonSerializer.SerializeToElement(new { state = "ready", name = row.GetProperty("name"), identity = row.GetProperty("identity") });
            binding = Apply(binding, ready, "zmx-reattached");
            string tokenData = string.Join('\0', endpoint.Distribution, endpoint.User, endpoint.Executable, endpoint.SocketDirectory,
                JsonSerializer.Serialize(WireIdentity(binding)));
            byte[] tokenBytes = SHA256.HashData(Encoding.UTF8.GetBytes(tokenData));
            string token = Convert.ToHexStringLower(tokenBytes);
            // Borrowed views of one incarnation share its lease operation; local
            // pane IDs remain independent, so repeated attach cannot leak keepers.
            if (operation is null) binding = binding with { BindingId = new Guid(tokenBytes.AsSpan(0, 16)).ToString() };
            sessions.Add(new(binding.Identity!.Name, token, binding.Ownership, row.GetProperty("attachments").GetUInt64(), binding));
        }
        return sessions;
    }

    public async Task<WslLiveBinding> BorrowAsync(WslLiveBinding binding, CancellationToken ct = default)
    {
        if (binding.Ownership == "owned") return await ReconcileAsync(binding, ct).ConfigureAwait(false);
        var result = await _client.InvokeAsync(Location(binding), new { verb = "borrow", ledger = binding.Endpoint.LedgerDirectory,
            operation = binding.BindingId, executable = binding.Endpoint.Executable, socketDirectory = binding.Endpoint.SocketDirectory,
            name = binding.Identity!.Name, identity = WireIdentity(binding) }, ct).ConfigureAwait(false);
        return Apply(binding, result, "zmx-reattached");
    }

    public Task<JsonElement> ScreenAsync(WslLiveBinding binding, bool all) => _client.InvokeAsync(Location(binding),
        new { verb = "screen", executable = binding.Endpoint.Executable, socketDirectory = binding.Endpoint.SocketDirectory,
            name = binding.Identity!.Name, identity = WireIdentity(binding), all }, default);

    public Task<JsonElement> TypeAsync(WslLiveBinding binding, string text) => _client.InvokeAsync(Location(binding),
        new { verb = "type", executable = binding.Endpoint.Executable, socketDirectory = binding.Endpoint.SocketDirectory,
            name = binding.Identity!.Name, identity = WireIdentity(binding), text }, default);

    public static WslLiveClient.Location Location(WslLiveBinding binding)
    {
        if (!binding.IsValid || binding.Endpoint.LedgerDirectory is not { } ledger || binding.Endpoint.HelperPath is not { } helper)
            throw new InvalidOperationException("Unsupported or incomplete live binding");
        return new(binding.Endpoint.Distribution, binding.Endpoint.User, "/", ledger, helper, binding.Endpoint.SocketDirectory);
    }

    public static object WireIdentity(WslLiveBinding binding)
    {
        var identity = binding.Identity ?? throw new InvalidOperationException("No live identity");
        static object Process(LinuxProcessIncarnation p) => new { pid = p.Pid, start = p.StartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture), boot = p.BootId, @namespace = p.PidNamespace };
        return new { daemon = Process(identity.Daemon), workload = Process(identity.Workload),
            socket = new[] { identity.SocketDevice, identity.SocketInode }, path = binding.Endpoint.SocketDirectory + "/" + identity.Name };
    }

    private static WslLiveBinding Apply(WslLiveBinding binding, JsonElement result, string success)
    {
        string state = result.GetProperty("state").GetString() ?? "uncertain";
        if (!result.TryGetProperty("identity", out var raw))
            return binding with { Recovery = state is "missing" or "terminated" ? "missing" : "uncertain" };
        static LinuxProcessIncarnation Process(JsonElement p) => new(p.GetProperty("pid").GetInt32(),
            p.GetProperty("boot").GetString()!, ulong.Parse(p.GetProperty("start").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            p.GetProperty("namespace").GetString()!);
        var socket = raw.GetProperty("socket");
        var identity = new WslDaemonIdentity(result.GetProperty("name").GetString()!, Process(raw.GetProperty("daemon")),
            Process(raw.GetProperty("workload")), socket[0].GetUInt64(), socket[1].GetUInt64());
        if (!identity.IsValid || raw.GetProperty("path").GetString() != binding.Endpoint.SocketDirectory + "/" + identity.Name
            || (binding.Identity is not null && binding.Identity != identity))
            throw new InvalidOperationException("Helper returned a different workload incarnation");
        return binding with { Identity = identity, Pending = false,
            Recovery = state == "ready" ? success : state is "missing" or "terminated" ? "missing" : "uncertain",
            Error = null, RestartOrigin = null };
    }
}
