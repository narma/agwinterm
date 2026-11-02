using System.Text.Json;

namespace Agwinterm.Pty;

public sealed record WslOriginExportRequest(WslOriginScope Scope, string AdapterExecutable)
{
    public static WslOriginExportRequest Parse(JsonElement args, string? target)
    {
        if (target is not null || args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Export selects an explicit endpoint and window, not a pane");
        string[] names = ["distribution", "user", "origin", "executable", "socketDirectory", "adapter"];
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in args.EnumerateObject())
            if (!names.Contains(property.Name) || property.Value.ValueKind != JsonValueKind.String
                || !WslLiveValidation.Text(property.Value.GetString(), 4096)
                || !fields.TryAdd(property.Name, property.Value.GetString()!))
                throw new ArgumentException("Unknown, invalid or duplicate export option");
        if (fields.Count != names.Length) throw new ArgumentException("Incomplete export endpoint");
        var scope = new WslOriginScope(fields["distribution"], fields["user"], fields["origin"],
            fields["executable"], fields["socketDirectory"]);
        if (!scope.IsValid || !WslLiveValidation.LinuxPath(fields["adapter"]) || fields["adapter"] == scope.Executable)
            throw new ArgumentException("Invalid export endpoint or attachment adapter");
        return new(scope, fields["adapter"]);
    }
}

/// <summary>Coordinates bounded probes outside the UI thread and refuses stale topology.</summary>
public static class WslOriginDiscovery
{
    public static async Task<string> DiscoverAsync(WslOriginExportRequest request,
        Func<IReadOnlyList<WslOriginSession>> capture,
        Func<WslLiveBinding, CancellationToken, Task<WslOriginObservation>> observe,
        CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        var snapshot = capture();
        // Validate size and topology before issuing any process requests. A snapshot has no
        // observations yet, so this validation cannot accidentally publish saved liveness.
        _ = WslOriginExport.Project(request.Scope, request.AdapterExecutable, snapshot);
        string before = JsonSerializer.Serialize(snapshot);
        var observed = new List<WslOriginSession>();
        foreach (var session in snapshot)
        {
            var panes = new List<WslOriginPane>();
            foreach (var pane in session.Panes)
            {
                token.ThrowIfCancellationRequested();
                WslOriginObservation? evidence = null;
                if (pane.Binding is { Identity: not null } binding)
                {
                    // Use the same eligibility filter to avoid probing borrowed/foreign panes.
                    var candidate = new WslOriginObservation(binding.BindingId, request.Scope, binding.Identity, true);
                    if (WslOriginExport.Eligible(request.Scope, pane with { Observation = candidate }))
                        try { evidence = await observe(binding, token).WaitAsync(token).ConfigureAwait(false); }
                        catch (WslRecoveryException) { /* A failed pane excludes its whole session. */ }
                }
                panes.Add(pane with { Observation = evidence });
            }
            observed.Add(session with { Panes = panes.ToArray() });
        }
        token.ThrowIfCancellationRequested();
        // Full snapshot equality fences close, replacement, role/layout and metadata changes.
        // No global generation counter needs to be maintained across all existing UI mutations.
        if (JsonSerializer.Serialize(capture()) != before)
            throw new InvalidOperationException("Origin topology changed during discovery; retry with a fresh snapshot");
        using var tree = JsonDocument.Parse(WslOriginExport.Project(request.Scope, request.AdapterExecutable, observed));
        var remote = tree.RootElement.GetProperty("result").GetProperty("remote");
        var exported = remote.GetProperty("sessions").EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToHashSet();
        // Private local relay evidence is stripped before the agterm tree is returned over SSH.
        var bindings = observed.Where(s => exported.Contains(s.Id)).SelectMany(s => s.Panes.Select(p => new
        {
            session = s.Id, window = s.WindowId, pane = p.Id, role = p.Role,
            operation = p.Binding!.BindingId, name = p.Binding.Identity!.Name,
            identity = WslLiveOperations.WireIdentity(p.Binding)
        })).ToArray();
        return JsonSerializer.Serialize(new { ok = true, result = new { remote, bindings } });
    }
}
