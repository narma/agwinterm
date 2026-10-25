using Agwinterm.Core;
using System.Text.Json;

namespace Agwinterm.Pty;

public sealed partial class ControlObservation
{
    private sealed record ObservedPane(StatusSnapshot Value, string? Owner, long Seq);
    private sealed record ObservedSession(string[] Panes, string Winner, StatusSnapshot Value, long Seq);
    private readonly Dictionary<string, ObservedPane> _panes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ObservedSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IDisposable> _subscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _paneTokens = new(StringComparer.Ordinal);

    public void ObservePane(string paneId, StatusSignal signal)
    {
        var token = new object();
        lock (_gate)
        {
            if (_closed) throw Error("window-gone", "window closed");
            if (!_paneTokens.TryAdd(paneId, token)) throw new ArgumentException("Pane already observed");
        }
        // Lock order is signal -> coordinator, never coordinator -> signal.
        var subscription = signal.Subscribe((value, changed) =>
        {
            lock (_gate)
            {
                if (_closed || !_paneTokens.TryGetValue(paneId, out var currentToken) || currentToken != token) return;
                _panes.TryGetValue(paneId, out var before);
                _panes[paneId] = new(value, before?.Owner, before?.Seq ?? _head);
                if (changed)
                {
                    var fields = new Dictionary<string, object?>
                    {
                        ["action"] = "changed", ["session"] = paneId, ["paneId"] = paneId,
                        ["info"] = Name(value.Status), ["status"] = Name(value.Status),
                        ["statusChangedAt"] = value.ChangedAt, ["blink"] = value.Blink, ["autoReset"] = value.AutoReset
                    };
                    if (before?.Owner is { } owner) fields["sessionId"] = owner;
                    // Predict the sequence under the same lock before publishing wakes waiters.
                    _panes[paneId] = _panes[paneId] with { Seq = checked(_head + 1) };
                    Publish("status", fields);
                }
                if (before?.Owner is { } sessionId && _sessions.TryGetValue(sessionId, out var session) && session.Panes.Contains(paneId))
                    AggregateLocked(sessionId, session.Panes);
            }
        });
        lock (_gate)
        {
            if (!_closed && _paneTokens.TryGetValue(paneId, out var currentToken) && currentToken == token && _subscriptions.TryAdd(paneId, subscription)) return;
        }
        subscription.Dispose();
    }

    public void SetOwner(string paneId, string? sessionId)
    {
        lock (_gate)
            if (_panes.TryGetValue(paneId, out var pane)) _panes[paneId] = pane with { Owner = sessionId };
    }

    public void SetMembership(string sessionId, IEnumerable<string> paneIds)
    {
        lock (_gate)
        {
            var ids = paneIds.ToArray();
            if (ids.Length is < 1 or > 2 || ids.Distinct().Count() != ids.Length || ids.Any(id => !_panes.ContainsKey(id)))
                throw new ArgumentException("Invalid observed session membership");
            foreach (string id in ids) _panes[id] = _panes[id] with { Owner = sessionId };
            AggregateLocked(sessionId, ids);
        }
    }

    private void AggregateLocked(string sessionId, string[] paneIds)
    {
        var winner = paneIds.OrderByDescending(id => StatusAggregate.Severity(_panes[id].Value.Status))
            .ThenByDescending(id => _panes[id].Value.ChangedAt).First();
        var value = _panes[winner].Value;
        _sessions.TryGetValue(sessionId, out var before);
        bool changed = before is not null && before.Value.Status != value.Status;
        _sessions[sessionId] = new(paneIds, winner, value, changed ? checked(_head + 1) : before?.Seq ?? _head);
        if (changed)
            Publish("session-status", new Dictionary<string, object?>
            {
                ["action"] = "changed", ["sessionId"] = sessionId, ["paneId"] = winner,
                ["winningPaneId"] = winner, ["status"] = Name(value.Status),
                ["previousStatus"] = Name(before!.Value.Status), ["statusChangedAt"] = value.ChangedAt
            });
    }

    public void RemovePane(string paneId)
    {
        IDisposable? subscription;
        lock (_gate)
        {
            _paneTokens.Remove(paneId);
            if (_panes.Remove(paneId, out var pane) && pane.Owner is { } owner && _sessions.TryGetValue(owner, out var session))
            {
                var remaining = session.Panes.Where(id => id != paneId).ToArray();
                if (remaining.Length == 0) _sessions.Remove(owner);
                else AggregateLocked(owner, remaining);
            }
            _subscriptions.Remove(paneId, out subscription);
            NotifyPendingLocked();
        }
        subscription?.Dispose();
    }

    public void RemoveSession(string sessionId)
    {
        lock (_gate)
        {
            _sessions.Remove(sessionId);
            NotifyPendingLocked();
        }
    }

    public (string? SessionId, string PaneId, StatusSnapshot Value)? Status(string scope, string id)
    {
        lock (_gate)
        {
            if (scope == "session" && _sessions.TryGetValue(id, out var session)) return (id, session.Winner, session.Value);
            if (scope == "pane" && _panes.TryGetValue(id, out var pane)) return (pane.Owner, id, pane.Value);
            return null;
        }
    }

    private static string Name(AgentStatus status) => status.ToString().ToLowerInvariant();

    public Task<StatusWaitResult> WaitAsync(string scope, string id, IReadOnlySet<AgentStatus> statuses,
        int timeoutMs = 30000, CancellationToken cancellationToken = default)
    {
        if (scope is not ("session" or "pane") || statuses.Count == 0 || statuses.Any(s => !Enum.IsDefined(s)) || timeoutMs < 0 || timeoutMs > 86400000)
            throw Error("invalid-argument", "invalid wait scope, statuses or timeout");
        lock (_gate)
        {
            if (_closed) throw Error("window-gone", "window closed");
            var initial = Status(scope, id) ?? throw Error("target-gone", "target no longer exists");
            long start = _clock();
            var accepted = statuses.ToHashSet();
            StatusWaitResult Result(string outcome, string? status = null, long? seq = null, string? source = null, string? winner = null) =>
                new(outcome, scope, Run, Window, initial.SessionId, winner ?? initial.PaneId, Math.Max(0, _clock() - start), status, seq, source);
            if (accepted.Contains(initial.Value.Status)) return Task.FromResult(Result("matched", Name(initial.Value.Status), _head, "snapshot"));
            var pending = new Pending<StatusWaitResult>(timeoutMs == 0 ? null : start + timeoutMs);
            pending.Evaluate = () =>
            {
                if (_closed) pending.Source.TrySetException(Error("window-gone", "window closed"));
                else if (pending.Deadline is { } deadline && _clock() >= deadline) pending.Source.TrySetResult(Result("timeout"));
                else if (Status(scope, id) is not { } current) pending.Source.TrySetException(Error("target-gone", "target removed"));
                else if (accepted.Contains(current.Value.Status))
                {
                    long seq = scope == "session" ? _sessions[id].Seq : _panes[id].Seq;
                    pending.Source.TrySetResult(Result("matched", Name(current.Value.Status), seq, "transition", current.PaneId));
                }
            };
            return RegisterLocked(pending, cancellationToken);
        }
    }
}

public sealed record StatusWaitResult(string Outcome, string Scope, string Run, string Window,
    string? SessionId, string PaneId, long ElapsedMs, string? Status = null, long? Seq = null, string? Source = null)
{
    public string ToJson()
    {
        var fields = new Dictionary<string, object?>
        {
            ["outcome"] = Outcome, ["scope"] = Scope, ["run"] = Run, ["window"] = Window,
            ["paneId"] = PaneId, ["elapsedMs"] = ElapsedMs
        };
        if (SessionId is not null) fields["sessionId"] = SessionId;
        if (Status is not null) { fields["status"] = Status; fields["seq"] = Seq; fields["source"] = Source; }
        return JsonSerializer.Serialize(fields);
    }
}
