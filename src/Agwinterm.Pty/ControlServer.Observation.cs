using System.Text.Json;
using Agwinterm.Core;

namespace Agwinterm.Pty;

public sealed partial class ControlServer
{
    public async Task<string> DispatchAsync(string requestJson, CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("cmd", out var cmd) || cmd.ValueKind != JsonValueKind.String || cmd.GetString() is not ("events" or "session.wait"))
                return Dispatch(requestJson);
            string? Selector(string key)
            {
                if (!root.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
                if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                    throw new ObservationException("invalid-argument", key + " must be a nonempty string");
                return value.GetString();
            }
            string? window = Selector("window"), target = Selector("target");
            if (window is not null && _windows is null) throw new ObservationException("unsupported", "window routing unavailable");
            if (_windows is not null && window is not (null or "active" or "quick"))
            {
                var windows = _windows.Windows().Where(w => w.Open).ToArray();
                if (!windows.Any(w => w.Id == window) && windows.Count(w => w.Id.StartsWith(window, StringComparison.Ordinal)) > 1)
                    throw new ObservationException("invalid-argument", "ambiguous window");
            }
            var host = _windows is null ? _host : _windows.ResolveWindow(window);
            if (host is null) throw new ObservationException("window-gone", "window not found");
            var auxiliary = host.AuxiliaryTarget(target);
            if (window is not null && auxiliary is not null && !ReferenceEquals(auxiliary, host))
                throw new ObservationException("invalid-argument", "target outside selected window");
            host = auxiliary ?? host;
            var args = root.TryGetProperty("args", out var a) ? a : default;
            if (cmd.GetString() == "session.wait")
                return OkRaw((await host.WaitStatusAsync(target, StatusWaitQuery.Parse(args), cancellationToken).ConfigureAwait(false)).ToJson());
            if (target is not null) throw new ObservationException("invalid-argument", "events does not accept a session target");
            var query = EventQuery.Parse(args);
            if (host.Observation is not { } observation)
            {
                if (query.Run is not null || query.Kinds is not null || query.Tail || query.WaitMs != 0)
                    throw new ObservationException("unsupported", "host does not support event observation");
                return OkRaw(host.Events(query.Since, query.Limit));
            }
            return OkRaw((await observation.ReadAsync(query, cancellationToken).ConfigureAwait(false)).ToJson());
        }
        catch (OperationCanceledException) { throw; }
        catch (ObservationException ex) { return ex.ToJson(); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        { return new ObservationException("invalid-argument", ex.Message).ToJson(); }
    }
}

public sealed record StatusWaitQuery(string Scope, IReadOnlySet<AgentStatus> Statuses, int TimeoutMs)
{
    public static StatusWaitQuery Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new ObservationException("invalid-argument", "wait args required");
        string scope = "session";
        int timeout = 30000;
        var statuses = new HashSet<AgentStatus>();
        var seen = new HashSet<string>();
        foreach (var prop in args.EnumerateObject())
        {
            if (!seen.Add(prop.Name)) throw new ObservationException("invalid-argument", "duplicate wait argument");
            var value = prop.Value;
            switch (prop.Name)
            {
                case "scope" when value.ValueKind == JsonValueKind.String && value.GetString() is "session" or "pane": scope = value.GetString()!; break;
                case "timeout-ms" when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out timeout) && timeout >= 0 && timeout <= 86400000: break;
                case "statuses" when value.ValueKind == JsonValueKind.Array:
                    foreach (var status in value.EnumerateArray())
                    {
                        if (status.ValueKind != JsonValueKind.String || status.GetString() is not ("idle" or "active" or "blocked" or "completed") ||
                            !statuses.Add(Enum.Parse<AgentStatus>(status.GetString()!, true)))
                            throw new ObservationException("invalid-argument", "statuses must contain unique lowercase status names");
                    }
                    break;
                default: throw new ObservationException("invalid-argument", "invalid wait argument: " + prop.Name);
            }
        }
        if (statuses.Count == 0) throw new ObservationException("invalid-argument", "nonempty statuses required");
        return new(scope, statuses, timeout);
    }
}
