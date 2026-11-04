using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agwinterm.Pty;

/// <summary>Fixture/model seam only. No SSH, discovery service or presentation transport.</summary>
public sealed record AgtermEndpoint(
    [property: JsonPropertyName("executable")] string Executable,
    [property: JsonPropertyName("socketDirectory")] string SocketDirectory);
public sealed record AgtermPane(
    [property: JsonPropertyName("pane")] string Role,
    [property: JsonPropertyName("daemon")] string Daemon)
{
    // Pinned upstream ZmxSupport/RemoteBinding encodes the stable pane UUID in this name.
    [JsonIgnore] public Guid? OriginPaneId => Daemon is not null && AgtermRemoteDiscovery.DaemonName(Daemon)
        && Guid.TryParseExact(Daemon[7..], "N", out var id) ? id : null;
}
public sealed record AgtermSession(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("windowID")] string WindowId,
    [property: JsonPropertyName("windowName")] string WindowName,
    [property: JsonPropertyName("workspaceID")] string WorkspaceId,
    [property: JsonPropertyName("workspaceName")] string WorkspaceName,
    [property: JsonPropertyName("cwd")] string Cwd,
    [property: JsonPropertyName("splitAxis")] string? SplitAxis,
    [property: JsonPropertyName("panes")] AgtermPane[] Panes);
public sealed record AgtermTree(
    [property: JsonPropertyName("endpoint")] AgtermEndpoint Endpoint,
    [property: JsonPropertyName("sessions")] AgtermSession[] Sessions);
public sealed record OriginPaneMapping(string LocalPaneId, string OriginId, string WindowId,
    string WorkspaceId, string SessionId, string Role, string Daemon, AgtermEndpoint Endpoint);

public static class ZmxCompatibility
{
    public const string Revision = "0716007052a6cedb8c43bb745ce6e5e7d3bc6d2b";
    public static bool SupportsSsh => false;
    public static bool SupportsPresentation => false;
    public static IReadOnlyList<OriginPaneMapping> Map(AgtermTree tree, string origin)
    {
        if (!WslLiveValidation.Text(origin) || !WslLiveValidation.LinuxPath(tree.Endpoint.Executable)
            || !WslLiveValidation.LinuxPath(tree.Endpoint.SocketDirectory)) throw new ArgumentException("Invalid origin endpoint");
        var mappings = new List<OriginPaneMapping>();
        foreach (var session in tree.Sessions)
            foreach (var pane in session.Panes)
            {
                if (pane.Role is not ("left" or "right") || pane.Daemon.Length != 39 || !pane.Daemon.StartsWith("agterm-", StringComparison.Ordinal)
                    || !pane.Daemon[7..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                    throw new ArgumentException("Invalid origin daemon or pane role");
                mappings.Add(new(Guid.NewGuid().ToString(), origin, session.WindowId, session.WorkspaceId, session.Id, pane.Role, pane.Daemon, tree.Endpoint));
            }
        return mappings;
    }
    public static AgtermPane ExportPane(OriginPaneMapping mapping) => new(mapping.Role, mapping.Daemon);
}

public sealed record ZmxRoleNotice(string Nonce, string Role, uint Generation)
{
    public static ZmxRoleNotice? Parse(string title)
    {
        if (!title.StartsWith("zmx-role;", StringComparison.Ordinal)) return null;
        var fields = title[9..].Split(':');
        if (fields.Length != 3 || !WslLiveValidation.Text(fields[0]) || fields[1] is not ("leader" or "follower" or "unowned")
            || !uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint generation)) return null;
        return new(fields[0], fields[1], generation);
    }
}

public sealed class ZmxRoleState(string nonce)
{
    public string Nonce { get; } = nonce;
    public string Role { get; private set; } = "unknown";
    public uint Generation { get; private set; }
    public bool Apply(ZmxRoleNotice notice)
    {
        if (notice.Nonce != Nonce || (Role != "unknown" && notice.Generation < Generation)) return false;
        Generation = notice.Generation;
        bool changed = Role != notice.Role;
        Role = notice.Role;
        return changed;
    }
}

public sealed record ZmxScreen(ulong Revision, int Columns, int Rows, int CursorColumn, int CursorRow, bool Alternate, string Text)
{
    public static ZmxScreen? Parse(string output)
    {
        int newline = output.IndexOf('\n');
        var fields = (newline < 0 ? output : output[..newline]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 6) return null;
        var values = new ulong[6];
        for (int i = 0; i < 6; i++)
            if (!ulong.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i])) return null;
        if (values[1] is 0 or > 65535 || values[2] is 0 or > 65535 || values[3] >= values[1] || values[4] >= values[2] || values[5] > 1) return null;
        return new(values[0], (int)values[1], (int)values[2], (int)values[3], (int)values[4], values[5] == 1, newline < 0 ? "" : output[(newline + 1)..]);
    }
}
