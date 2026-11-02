using System.Text;
using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>The execution namespace, independent of per-binding endpoint IDs.</summary>
public sealed record WslOriginScope(string Distribution, string User, string OriginId,
    string Executable, string SocketDirectory)
{
    public static WslOriginScope From(WslLiveEndpoint endpoint) => new(endpoint.Distribution,
        endpoint.User, endpoint.OriginId, endpoint.Executable, endpoint.SocketDirectory);

    public bool IsValid => new WslLiveEndpoint("00000000-0000-0000-0000-000000000001",
        Distribution, User, Executable, SocketDirectory, OriginId).IsValid;
}

/// <summary>Independent native probe evidence; saved checkpoint state is not an observation.</summary>
public sealed record WslOriginObservation(string BindingId, WslOriginScope Scope,
    WslDaemonIdentity Identity, bool Ready);

public sealed record WslOriginPane(string Id, string Role, WslLiveBinding? Binding,
    WslOriginObservation? Observation, bool Attached, bool Closing = false);

public sealed record WslOriginSession(string Id, string Name, string WindowId, string WindowName,
    string WorkspaceId, string WorkspaceName, string Cwd, string? SplitAxis,
    IReadOnlyList<WslOriginPane> Panes);

/// <summary>
/// Pure projection boundary. Callers must obtain fresh native observations and revalidate their
/// UI snapshot before publication. This model does not provision or advertise a working adapter.
/// </summary>
public static class WslOriginExport
{
    public static bool Eligible(WslOriginScope scope, WslOriginPane pane)
    {
        var binding = pane.Binding;
        var observed = pane.Observation;
        return scope.IsValid && pane.Attached && !pane.Closing && pane.Role is "left" or "right"
            && binding is { IsValid: true, Ownership: "owned", Pending: false, Identity: not null }
            && binding.OriginPaneId == pane.Id
            && binding.Recovery is "host-adopted" or "zmx-reattached" or "fresh-started"
            && WslOriginScope.From(binding.Endpoint) == scope
            && observed is { Ready: true, Identity.IsValid: true }
            && observed.Scope == scope && observed.BindingId == binding.BindingId
            && observed.Identity == binding.Identity;
    }

    public static string Project(WslOriginScope scope, string adapterExecutable,
        IReadOnlyList<WslOriginSession> snapshot)
    {
        if (!scope.IsValid || !WslLiveValidation.LinuxPath(adapterExecutable)
            || adapterExecutable == scope.Executable)
            throw new ArgumentException("An explicit origin scope and separate attachment adapter are required");
        if (snapshot.Count > AgtermRemoteDiscovery.MaxSessions)
            throw new ArgumentException("Origin snapshot exceeds session limit");

        var sessions = new List<AgtermSession>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paneIds = new HashSet<string>(StringComparer.Ordinal);
        var bindings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in snapshot)
        {
            if (!ids.Add(session.Id)) throw new ArgumentException("Duplicate origin session ID");
            if (session.Panes.Count is < 1 or > 2
                || session.Panes.Count(p => p.Role == "left") != 1
                || (session.Panes.Count == 2
                    ? session.Panes.Count(p => p.Role == "right") != 1 || session.SplitAxis is not ("vertical" or "horizontal")
                    : session.SplitAxis is not null))
                throw new ArgumentException("Invalid origin pane topology");
            if (!session.Panes.All(p => Eligible(scope, p))) continue;
            foreach (var pane in session.Panes)
                if (!paneIds.Add(pane.Id) || !bindings.Add(pane.Binding!.BindingId))
                    throw new ArgumentException("Duplicate exported pane or binding ID");
            var panes = session.Panes.OrderBy(p => p.Role == "left" ? 0 : 1)
                .Select(p => new AgtermPane(p.Role, p.Binding!.Identity!.Name)).ToArray();
            sessions.Add(new(session.Id, session.Name, session.WindowId, session.WindowName,
                session.WorkspaceId, session.WorkspaceName, session.Cwd, session.SplitAxis, panes));
        }

        using var output = new BoundedOutput();
        JsonSerializer.Serialize(output, new { ok = true, result = new { remote = new AgtermTree(
            new(adapterExecutable, scope.SocketDirectory), sessions.ToArray()) } });
        string json = Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        // Both directions share the real envelope contract, including duplicate daemon and
        // window/workspace consistency checks. Presentation is deliberately absent.
        _ = AgtermRemoteDiscovery.Decode(0, json);
        return json;
    }

    private sealed class BoundedOutput : MemoryStream
    {
        private void Check(int count)
        {
            if (Length + count > AgtermRemoteDiscovery.MaxBytes)
                throw new ArgumentException("Origin tree exceeds output limit");
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }
        public override void WriteByte(byte value)
        {
            Check(1);
            base.WriteByte(value);
        }
    }
}
