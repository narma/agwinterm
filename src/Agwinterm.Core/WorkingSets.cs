namespace Agwinterm.Core;

public sealed record WorkingSession(string Id, string Name, bool Flagged);
public sealed record WorkingWorkspace(string Id, string Name, bool Expanded, IReadOnlyList<WorkingSession> Sessions);
public sealed record WorkingRow(string WorkspaceId, string? SessionId, string Label);
public sealed record WorkingFocus(string[] Ids, bool Enabled);

public static class WorkingSets
{
    public static string Layout(string? value) => value is "flat" or "tree" ? value : "plain";
    public static WorkingFocus LoadFocus(IEnumerable<string> liveIds, IEnumerable<string>? saved, bool? enabled, string? legacy)
    {
        var wanted = new HashSet<string>(saved ?? (legacy is null ? [] : new[] { legacy }), StringComparer.Ordinal);
        return new(liveIds.Where(wanted.Contains).Distinct(StringComparer.Ordinal).ToArray(), enabled ?? legacy is not null);
    }
    public static IReadOnlyList<WorkingRow> Rows(IEnumerable<WorkingWorkspace> workspaces, bool flagged, string layout,
        IEnumerable<string> focusIds, bool filter)
    {
        var focus = new HashSet<string>(focusIds, StringComparer.Ordinal);
        var rows = new List<WorkingRow>();
        foreach (var ws in workspaces)
        {
            if (!flagged && filter && focus.Count > 0 && !focus.Contains(ws.Id)) continue;
            var sessions = ws.Sessions.Where(s => !flagged || s.Flagged).ToArray();
            if (flagged && sessions.Length == 0) continue;
            bool grouped = !flagged || layout == "tree";
            if (grouped) rows.Add(new(ws.Id, null, ws.Name));
            if (grouped && !ws.Expanded) continue;
            rows.AddRange(sessions.Select(s => new WorkingRow(ws.Id, s.Id, flagged && layout == "flat" ? s.Name + " : " + ws.Name : s.Name)));
        }
        return rows;
    }
    public static string? Resolve(string selector, IEnumerable<(string Id, string Name)> candidates)
    {
        var all = candidates.ToArray();
        if (all.Any(s => s.Id == selector)) return selector;
        var prefix = all.Where(s => s.Id.StartsWith(selector, StringComparison.Ordinal)).ToArray();
        if (prefix.Length > 0) return prefix.Length == 1 ? prefix[0].Id : null;
        var named = all.Where(s => string.Equals(s.Name, selector, StringComparison.OrdinalIgnoreCase)).ToArray();
        return named.Length == 1 ? named[0].Id : null;
    }
}
