using System.Text;
using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>Discovery is inventory, never evidence of an incarnation or permission to attach.</summary>
public sealed record AgtermRemoteSession(AgtermSession Session, string? Content, string? Parent)
{
    public bool CanImportTerminal => Content is null && Parent is null;
    public string? UnsupportedReason => Content is not null ? "Unsupported remote content" :
        Parent is not null ? "Companion import is not supported" : null;
}

public sealed record AgtermRemoteInventory(AgtermEndpoint Endpoint, IReadOnlyList<AgtermRemoteSession> Sessions,
    int? PresentationVersion = null);

public sealed class AgtermDiscoveryException(string reason) : IOException(reason);

/// <summary>Decodes the pinned agterm ControlResponse/result.remote contract, not the old model fixture.</summary>
public static class AgtermRemoteDiscovery
{
    public const string Revision = "9133258994a6cafb330ee354df949b76b1b2834b";
    public const int MaxBytes = 4 * 1024 * 1024;
    public const int MaxSessions = 4096;

    public static AgtermRemoteInventory Decode(int exitCode, string stdout)
    {
        // A successful-looking body from a failed process must never offer attachment candidates.
        if (exitCode != 0) throw new AgtermDiscoveryException("Remote discovery command failed");
        if (stdout.Length > MaxBytes || Encoding.UTF8.GetByteCount(stdout) > MaxBytes)
            throw new AgtermDiscoveryException("Remote discovery exceeds output limit");
        try
        {
            using var document = JsonDocument.Parse(stdout, new() { MaxDepth = 32 });
            var root = document.RootElement;
            RejectDuplicates(root);
            if (Required(root, "ok").ValueKind != JsonValueKind.True)
                throw new AgtermDiscoveryException("Origin refused discovery");
            var remote = Required(Required(root, "result"), "remote");
            // Optional presentation metadata cannot invalidate an otherwise usable terminal inventory.
            int? presentation = remote.TryGetProperty("presentation", out var advertised)
                && advertised.ValueKind == JsonValueKind.Number && advertised.TryGetInt32(out var version)
                && version > 0 ? version : null;
            var endpoint = Required(remote, "endpoint");
            var executable = Text(endpoint, "executable", 4096);
            var socket = Text(endpoint, "socketDirectory", 4096);
            if (!OriginPath(executable) || !OriginPath(socket)) Invalid();
            var rows = Required(remote, "sessions");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > MaxSessions) Invalid();
            var sessions = new List<AgtermRemoteSession>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var daemons = new HashSet<string>(StringComparer.Ordinal);
            var windows = new Dictionary<string, string>(StringComparer.Ordinal);
            var workspaces = new Dictionary<string, (string Window, string Name)>(StringComparer.Ordinal);
            foreach (var row in rows.EnumerateArray())
            {
                string id = Text(row, "id"), window = Text(row, "windowID"), workspace = Text(row, "workspaceID");
                if (!ids.Add(id)) Invalid();
                string name = Text(row, "name", 4096, true), windowName = Text(row, "windowName", 4096, true);
                string workspaceName = Text(row, "workspaceName", 4096, true), cwd = Text(row, "cwd", 4096, true);
                if (windows.TryGetValue(window, out var oldName) && oldName != windowName) Invalid();
                windows[window] = windowName;
                if (workspaces.TryGetValue(workspace, out var oldWorkspace) && oldWorkspace != (window, workspaceName)) Invalid();
                workspaces[workspace] = (window, workspaceName);
                string? content = OptionalText(row, "content"), parent = OptionalText(row, "parent");
                string? axis = OptionalText(row, "splitAxis");
                var rawPanes = Required(row, "panes");
                if (rawPanes.ValueKind != JsonValueKind.Array || rawPanes.GetArrayLength() > 2) Invalid();
                var panes = new Dictionary<string, AgtermPane>(StringComparer.Ordinal);
                foreach (var raw in rawPanes.EnumerateArray())
                {
                    string role = Text(raw, "pane"), daemon = Text(raw, "daemon");
                    if (role is not ("left" or "right") || !DaemonName(daemon) || !daemons.Add(daemon)
                        || !panes.TryAdd(role, new(role, daemon))) Invalid();
                }
                if (content is null)
                {
                    if (!panes.ContainsKey("left") || (panes.Count == 2
                        ? axis is not ("vertical" or "horizontal") : axis is not null)) Invalid();
                }
                else if (panes.Count != 0 || axis is not null) Invalid();
                if (parent == id || (content == "page" && parent is null)) Invalid();
                // Origin order is irrelevant. Raw axis names already match our wire convention.
                AgtermPane[] ordered = panes.Count == 0 ? [] : panes.ContainsKey("right")
                    ? [panes["left"], panes["right"]] : [panes["left"]];
                sessions.Add(new(new(id, name, window, windowName, workspace, workspaceName, cwd, axis, ordered), content, parent));
            }
            return new(new(executable, socket), sessions.AsReadOnly(), presentation);
        }
        catch (JsonException) { throw new AgtermDiscoveryException("Malformed remote discovery response"); }
    }

    internal static bool OriginPath(string value) => WslLiveValidation.Text(value, 4096) && value.StartsWith('/');
    internal static bool DaemonName(string value) => value.Length == 39 && value.StartsWith("agterm-", StringComparison.Ordinal)
        && value[7..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static JsonElement Required(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var field))
            throw new AgtermDiscoveryException("Missing remote discovery field: " + key);
        return field;
    }

    private static string Text(JsonElement value, string key, int limit = 256, bool allowEmpty = false)
    {
        var field = Required(value, key);
        if (field.ValueKind != JsonValueKind.String) Invalid();
        string text = field.GetString()!;
        if (text.Length > limit || text.Any(char.IsControl) || (!allowEmpty && string.IsNullOrWhiteSpace(text))) Invalid();
        return text;
    }

    private static string? OptionalText(JsonElement value, string key) =>
        value.TryGetProperty(key, out var field) && field.ValueKind != JsonValueKind.Null ? Text(value, key) : null;

    internal static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) Invalid();
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }

    private static void Invalid() => throw new AgtermDiscoveryException("Invalid or ambiguous remote discovery response");
}
