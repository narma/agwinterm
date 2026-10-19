using Agwinterm.Core;
using Agwinterm.Pty;
using System.Text.Json;

namespace Agwinterm.Win32;

internal partial class Program : IWorkingSetHost
{
    private bool FocusFilterActive => _workspaceFilterEnabled && _focusedWorkspaceIds.Count > 0;
    private IReadOnlyList<WorkingRow> WorkingRows() => WorkingSets.Rows(_workspaces.Select(w => new WorkingWorkspace(w.Id, w.Name,
        w.Expanded, w.Sessions.Select(s => new WorkingSession(s.Id, s.SidebarName, s.Flagged)).ToArray())),
        _sidebarMode == SidebarMode.Flagged, _flaggedLayout, _focusedWorkspaceIds, _workspaceFilterEnabled);
    private List<Ses> VisibleSessions()
    {
        var all = AllSessions().ToDictionary(s => s.Id);
        return WorkingRows().Where(r => r.SessionId is not null).Select(r => all[r.SessionId!]).ToList();
    }
    public object WorkingSetState() => InvokeOnUiQueued(WorkingSetStateCore);
    private object WorkingSetStateCore() => new
    {
        focusedWorkspaceIds = _workspaces.Where(w => _focusedWorkspaceIds.Contains(w.Id)).Select(w => w.Id).ToArray(),
        workspaceFilterEnabled = _workspaceFilterEnabled, sidebarFlaggedLayout = _flaggedLayout,
        visibleSessionIds = WorkingRows().Where(r => r.SessionId is not null).Select(r => r.SessionId).ToArray()
    };
    private void SetWorkspaceFilter(bool enabled)
    {
        _workspaceFilterEnabled = enabled;
        ClearExcludedWorkspaceTarget();
        RequestRedraw(); SaveState(); EmitEvent("tree");
    }
    private void DisableWorkspaceFilter() => SetWorkspaceFilter(false);
    private void ClearWorkspaceFocus() { _focusedWorkspaceIds.Clear(); DisableWorkspaceFilter(); }
    private void ClearExcludedWorkspaceTarget()
    {
        if (FocusFilterActive && _workspaceTarget is { } target && !_focusedWorkspaceIds.Contains(target.Id))
            _workspaceTarget = null;
    }
    private void SetWorkspaceMembership(string op, string id)
    {
        if (op == "clear") { ClearWorkspaceFocus(); return; }
        bool member = op == "on" || op == "toggle" && !_focusedWorkspaceIds.Contains(id);
        if (member) { _focusedWorkspaceIds.Add(id); _workspaceFilterEnabled = true; }
        else _focusedWorkspaceIds.Remove(id);
        ClearExcludedWorkspaceTarget();
        RequestRedraw(); SaveState(); EmitEvent("tree");
    }
    private Workspace WorkingWorkspace(string? selector) => selector is null or "active" ? ActiveWorkspace() :
        _workspaces.FirstOrDefault(w => w.Id == WorkingSets.Resolve(selector, _workspaces.Select(w => (w.Id, w.Name))))
            ?? throw new ArgumentException("unknown or ambiguous workspace");
    private Ses? WorkingSession(string selector) => selector == "active" ? _active :
        FindSessionByExactId(WorkingSets.Resolve(selector, AllSessions().Select(s => (s.Id, s.Name))) ?? "");

    public object WorkingSetCommand(string command, JsonElement args, string? target) => InvokeOnUiQueued(() =>
    {
        WorkingSetRequest.Validate(command, args, target);
        if (_isQuickWindow) throw new ArgumentException("working sets require a library window");
        string? S(string key) => WorkingSetRequest.String(args, key);
        bool B(string key) => WorkingSetRequest.Bool(args, key);
        string op = S("op") ?? "toggle";
        if (command == "workspace.new")
        {
            var ws = CreateWorkspace(Guid.NewGuid().ToString(), S("name")); ws.Expanded = !B("collapsed");
            string? defaultDir = S(WorkspaceDefaultDirs.Key) ?? S("default-dir");
            ws.DefaultDir = defaultDir is null ? null : WorkspaceDefaultDirs.NormalizeSet(defaultDir);
            SaveState(); RequestRedraw(); EmitEvent("tree"); return (object)ws.Id;
        }
        if (command == "workspace.flag")
        {
            var ws = WorkingWorkspace(target);
            int changed = 0;
            foreach (var s in ws.Sessions)
            { bool value = op == "on" || op == "toggle" && !s.Flagged; if (s.Flagged != value) { s.Flagged = value; changed++; } }
            if (changed > 0) { SaveState(); RequestRedraw(); EmitEvent("tree"); }
            return new { workspaceId = ws.Id, changed, count = ws.Sessions.Count };
        }
        if (command == "workspace.focus")
        {
            if (op == "clear") ClearWorkspaceFocus();
            else if (target is null) WorkspaceFocusOp(op);
            else SetWorkspaceMembership(op, WorkingWorkspace(target).Id);
            // Preserve the original untargeted focus command's string reply.
            return target is null && op != "clear" ? (object)"focus" : WorkingSetStateCore();
        }
        if (command == "workspace.filter")
        {
            SetWorkspaceFilter(op == "on" || op == "toggle" && !_workspaceFilterEnabled);
            return WorkingSetStateCore();
        }
        if (command == "sidebar.flagged-layout")
        {
            string layout = S("layout") ?? "toggle";
            _flaggedLayout = layout == "toggle" ? _flaggedLayout == "tree" ? "flat" : "tree" : layout;
            SaveState(); RequestRedraw(); EmitEvent("tree"); return WorkingSetStateCore();
        }
        Ses? anchor = S("before") is { } before ? WorkingSession(before) ?? throw new ArgumentException("unknown or ambiguous before anchor") :
            S("after") is { } after ? WorkingSession(after) ?? throw new ArgumentException("unknown or ambiguous after anchor") : null;
        Workspace? destination = S("workspace") is { } wsName ? WorkingWorkspace(wsName) :
            S("workspace-name") is { } byName ? _workspaces.SingleOrDefault(w => string.Equals(w.Name, byName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("unknown workspace name") : anchor?.Ws;
        if (anchor is not null && destination != anchor.Ws) throw new ArgumentException("workspace and anchor disagree");
        if (command == "session.new")
        {
            if (anchor is null || destination is null) throw new ArgumentException("anchored new requires an anchor");
            if (!SessionCommand.TryCreate(S("command"), S("command-mode"), B("wait"), S("profile"), out var launch, out var error))
                throw new ArgumentException(error);
            if (S("profile") is { } profile && !_profileCfg.Profiles.Any(p => p.Name.Equals(profile, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("unknown profile");
            if (ResolveProfile(S("profile"))?.Elevate == true && !IsElevated()) throw new ArgumentException("anchored creation requires an elevated destination for this profile");
            var created = CreateSession(Guid.NewGuid().ToString(), S("name"), S("cwd"), destination, !B("no-select"), profileName: S("profile"), sessionCommand: launch);
            destination.Sessions.Remove(created);
            destination.Sessions.Insert(destination.Sessions.IndexOf(anchor) + (S("after") is null ? 0 : 1), created);
            SaveState(); RequestRedraw(); EmitEvent("tree"); return created.Id;
        }
        var requested = WorkingSetRequest.Targets(args) ?? [target ?? "active"];
        var resolved = requested.Select(WorkingSession).ToArray();
        var known = resolved.OfType<Ses>().ToArray();
        if (WorkingSetRequest.Targets(args) is null && known.Length == 0) throw new ArgumentException("unknown, ambiguous or unsupported session");
        var previous = known.ToDictionary(s => s, s => (Workspace: s.Ws, Index: s.Ws.Sessions.IndexOf(s)));
        if (known.Distinct().Count() != known.Length) throw new ArgumentException("duplicate resolved sessions in batch");
        if (anchor is not null && known.Contains(anchor)) throw new ArgumentException("anchor cannot be moved in its own batch");
        if (command == "session.move" && destination is null) throw new ArgumentException("missing destination workspace");
        var outcomes = new List<object>(); bool partial = false;
        int insertion = anchor is null ? destination?.Sessions.Count ?? 0 : destination!.Sessions.IndexOf(anchor) + (S("after") is null ? 0 : 1);
        if (command == "session.move")
        {
            // Remove all members first, adjusting the insertion boundary for members before it.
            foreach (var s in known)
            {
                if (ReferenceEquals(s.Ws, destination) && s.Ws.Sessions.IndexOf(s) < insertion) insertion--;
                lock (_workspaces) s.Ws.Sessions.Remove(s);
            }
        }
        for (int i = 0; i < requested.Length; i++)
        {
            var s = resolved[i];
            if (s is null) { partial = true; outcomes.Add(new { target = requested[i], sessionId = (string?)null, changed = false, error = "unknown, ambiguous or unsupported session" }); continue; }
            if (command == "session.close") CloseSessionInternal(s);
            else
            {
                s.Ws = destination!;
                lock (_workspaces) destination!.Sessions.Insert(Math.Clamp(insertion++, 0, destination.Sessions.Count), s);
            }
            bool changed = command == "session.close" || previous[s].Workspace != s.Ws || previous[s].Index != s.Ws.Sessions.IndexOf(s);
            outcomes.Add(new { target = requested[i], sessionId = s.Id, changed, error = (string?)null });
        }
        SaveState(); RequestRedraw(); EmitEvent("tree");
        return new { outcome = partial ? "partial" : "completed", items = outcomes };
    });
}
