namespace Agwinterm.Win32;

internal partial class Program
{
    private sealed record SoftWebMember(Ses Session, int Index, Workspace Workspace, int WorkspaceIndex, string? RecentId);
    private sealed class SoftWebClose(string entryId, SoftWebMember[] members, string? selectedId)
    {
        public string EntryId = entryId;
        public SoftWebMember[] Members = members;
        public string? SelectedId = selectedId;
        public Workspace? RemovedWorkspace;
        public int WorkspaceIndex;
        public bool FocusMember;
    }
    private readonly List<SoftWebClose> _softWebCloses = new();

    private bool TrySoftCloseWebSession(Ses owner) => TrySoftCloseWebSessions([owner]);

    private bool TrySoftCloseWebSessions(IEnumerable<Ses> owners, Workspace? workspace = null)
    {
        if (!BrowserPreferences.UndoClose) return false;
        var roots = owners.Where(s => !s.Closing).ToHashSet();
        var members = AllSessions().Where(s => roots.Contains(s) || roots.Any(p => s.PageParentId == p.Id)).ToArray();
        var parked = workspace is null ? [] : _softWebCloses.Where(c => c.Members.Any(m => m.Session.Ws == workspace)).ToArray();
        if (!members.Any(s => _webPages.Any(pair => ReferenceEquals(pair.Value.Owner, s))) && parked.Length == 0) return false;
        string? selected = _active?.Id;
        var captured = members.Select(s =>
        {
            if (workspace is null) CaptureClosedSession(s);
            return new SoftWebMember(s, s.Ws.Sessions.IndexOf(s), s.Ws, _workspaces.IndexOf(s.Ws),
                workspace is null && s.PageParentId is null ? _closedSessions.LastOrDefault(r => r.SessionId == s.Id)?.EntryId : null);
        }).ToList();
        foreach (var pending in parked)
        {
            // Transfer this workspace's parked members without extending unrelated groups.
            var moved = pending.Members.Where(m => m.Session.Ws == workspace).ToArray();
            captured.AddRange(moved);
            pending.Members = pending.Members.Except(moved).ToArray();
            if (pending.Members.Length == 0) _softWebCloses.Remove(pending);
        }
        string entry = captured.FirstOrDefault(m => m.RecentId is not null)?.RecentId ?? Guid.NewGuid().ToString();
        if (workspace is not null)
        {
            CaptureClosedWorkspace(workspace, captured.Select(m => m.Session).ToArray());
            entry = _closedWorkspaces.LastOrDefault(w => w.WorkspaceId == workspace.Id)?.EntryId ?? entry;
            _closedSessions.RemoveAll(r => captured.Any(m => m.RecentId == r.EntryId));
        }
        var close = new SoftWebClose(entry, captured.ToArray(), selected)
        {
            RemovedWorkspace = workspace, WorkspaceIndex = workspace is null ? 0 : _workspaces.IndexOf(workspace),
            FocusMember = workspace is not null && _focusedWorkspaceIds.Contains(workspace.Id)
        };
        _softWebCloses.Add(close);
        foreach (var session in members)
        {
            DetachSessionUi(session);
            session.Closing = true;
            foreach (var (_, page) in _webPages.Where(pair => ReferenceEquals(pair.Value.Owner, session))) CancelWebExternal(page);
            lock (_workspaces) session.Ws.Sessions.Remove(session);
            _selectedIds.Remove(session.Id); _mru.Remove(session.Id);
        }
        if (workspace is not null)
        {
            lock (_workspaces) _workspaces.Remove(workspace);
            _focusedWorkspaceIds.Remove(workspace.Id);
            if (ReferenceEquals(_workspaceTarget, workspace)) _workspaceTarget = null;
            if (ReferenceEquals(_editing, workspace)) CancelRename();
            if (ReferenceEquals(_dragItem, workspace) || ReferenceEquals(_pressItem, workspace))
            { _dragging = false; _sbPress = false; _dragItem = null; _pressItem = null; Win32.ReleaseCapture(); }
        }
        if (members.Contains(_active!))
        {
            _cover = null; _coverKind = 0; _ovlOwner = null;
            var next = _mru.Select(FindSes).FirstOrDefault(s => s is not null) ?? AllSessions().FirstOrDefault();
            if (next is null) CreateSession(Guid.NewGuid().ToString(), null, null, ActiveWorkspace(), true);
            else SetActive(next);
        }
        RefreshHudTimer(); UpdateWebPresentation(); RequestRedraw(); SaveRecent(); SaveState(); EmitEvent("tree");
        ShowToast("Closed — Reopen Last Closed Item restores it for 3 seconds", 3000);
        _ = Task.Delay(3000).ContinueWith(_ => PostVerb(() => FinalizeSoftWebClose(close)));
        return true;
    }

    private void FinalizeParkedWebCompanions(string parentId)
    {
        foreach (var close in _softWebCloses.ToArray())
        {
            var children = close.Members.Where(m => m.Session.PageParentId == parentId).ToArray();
            close.Members = close.Members.Except(children).ToArray();
            foreach (var member in children) DisposeSessionResources(member.Session);
            if (close.Members.Length == 0) _softWebCloses.Remove(close);
        }
    }

    private void FinalizeSoftWebClose(SoftWebClose close)
    {
        if (!_softWebCloses.Remove(close)) return;
        foreach (var member in close.Members)
        { FinalizeParkedWebCompanions(member.Session.Id); DisposeSessionResources(member.Session); }
    }

    private bool RestoreSoftWebClose(string? entryId, out object? result)
    {
        result = null;
        var close = entryId is null ? _softWebCloses.LastOrDefault() : _softWebCloses.LastOrDefault(c =>
            c.EntryId == entryId || c.Members.Any(m => m.RecentId == entryId));
        if (close is null) return false;
        _softWebCloses.Remove(close);
        if (close.RemovedWorkspace is { } removed)
        {
            if (!_workspaces.Any(w => w.Id == removed.Id))
                lock (_workspaces) _workspaces.Insert(Math.Clamp(close.WorkspaceIndex, 0, _workspaces.Count), removed);
            if (close.FocusMember) _focusedWorkspaceIds.Add(removed.Id);
        }
        foreach (var member in close.Members.OrderBy(m => m.Session.PageParentId is not null).ThenBy(m => m.Index))
        {
            var session = member.Session;
            var ws = session.PageParentId is { } parent ? FindSes(parent)?.Ws : null;
            ws ??= _workspaces.FirstOrDefault(w => w.Id == member.Workspace.Id);
            if (ws is null)
            {
                ws = member.Workspace;
                lock (_workspaces) _workspaces.Insert(Math.Clamp(member.WorkspaceIndex, 0, _workspaces.Count), ws);
            }
            session.Ws = ws; session.Closing = false;
            lock (_workspaces) ws.Sessions.Insert(Math.Clamp(member.Index, 0, ws.Sessions.Count), session);
            ObserveLayout(session);
        }
        _closedSessions.RemoveAll(s => close.Members.Any(m => m.RecentId == s.EntryId));
        _closedWorkspaces.RemoveAll(w => w.EntryId == close.EntryId);
        var active = close.Members.FirstOrDefault(m => m.Session.Id == close.SelectedId)?.Session ?? close.Members[0].Session;
        SetActive(active);
        UpdateWebPresentation(); SaveRecent(); SaveState(); RequestRedraw(); EmitEvent("tree");
        result = new { entryId = close.EntryId, outcome = "completed", windowId = Id, workspaceId = active.Ws.Id,
            members = close.Members.Select(m => new { originalId = m.Session.Id, sessionId = m.Session.Id, paneIds = m.Session.AllPanes.Select(p => p.Id).ToArray(), error = (string?)null }), remaining = 0, warning = Recent.Warning };
        return true;
    }
}
