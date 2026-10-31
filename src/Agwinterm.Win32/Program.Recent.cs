using Agwinterm.Pty;
using Agwinterm.Core;

namespace Agwinterm.Win32;

internal partial class Program : IRecentHost
{
    private static RecentHistory? _recent;
    private static RecentHistory _recentBaseline = new();
    private static RecentHistoryWriter? _recentWriter;
    private static long _recentRevision;
    private static string RecentPath => Path.Combine(AppDir, "recent.json");
    private static RecentHistory Recent
    {
        get
        {
            if (_recent is not null) return _recent;
            _recent = new();
            _recentWriter = new(RecentPath);
            ObserveRecentWrite(_recentWriter.ReadAsync(), _recentRevision);
            return _recent;
        }
    }
    private static List<ClosedSession> _closedSessions => Recent.Sessions;
    private static List<ClosedWorkspace> _closedWorkspaces => Recent.Workspaces;
    private static void ObserveRecentWrite(Task<RecentHistory> task, long revision)
    {
        _ = task.ContinueWith(done =>
        {
            var error = done.Exception?.GetBaseException().Message;
            try { LiveWindow().Post(() =>
            {
                if (error is not null) { Recent.Warning = "Recent history not saved: " + error; LiveWindow().ShowToast(Recent.Warning); }
                else if (_recentRevision == revision)
                {
                    _recent = done.Result; _recentBaseline = _recent.Copy();
                    foreach (var window in _byId.Values) window.RequestRedraw();
                }
            }); }
            catch { System.Diagnostics.Debug.WriteLine(error); }
        }, TaskScheduler.Default);
    }

    private void SaveRecent(bool clearAll = false)
    {
        Recent.Bound();
        var snapshot = Recent.Copy();
        ObserveRecentWrite(_recentWriter!.Enqueue(_recentBaseline, snapshot, clearAll), ++_recentRevision);
        _recentBaseline = snapshot;
    }

    // Called on control-pipe threads: wait for earlier queued operations and read fresh shared
    // history without making the UI thread wait for locks, file filters, or disk writes.
    private static void RefreshRecent()
    {
        var owner = LiveWindow();
        long revision = owner.InvokeOnUiQueued(() => { _ = Recent; return ++_recentRevision; });
        try
        {
            var snapshot = _recentWriter!.ReadAsync().GetAwaiter().GetResult();
            owner.InvokeOnUiQueued(() =>
            {
                if (_recentRevision == revision) { _recent = snapshot; _recentBaseline = snapshot.Copy(); }
                return true;
            });
        }
        catch (Exception e)
        {
            owner.InvokeOnUiQueued(() => { Recent.Warning = "Recent history not saved: " + e.Message; return true; });
        }
    }

    private ClosedSession CaptureClosedSessionData(Ses s)
    {
        var state = new SessionState { Id = s.Id, Name = s.Name, CustomName = s.CustomName, Profile = s.ProfileName,
            Context = s.Context, Active = s.HiddenSplit is null ? s.Active : 1 - s.HiddenSplitIndex,
            SplitHidden = s.HiddenSplit is null ? null : true,
            Flagged = s.Flagged, Axis = RestoreState.StoreAxis(s.AllPanes.Count(), s.Axis) };
        foreach (var p in s.AllPanes)
        {
            string cwd = PrettyCwd(SafeCwd(p));
            if (string.IsNullOrEmpty(cwd)) cwd = p.StartCwd ?? "";
            state.Panes.Add(new() { Id = p.Id, Cwd = cwd, FontSize = p.FontSize, FontZoomed = p.FontZoomed,
                Ratio = s.StoredRatio(p), Appearance = p.Appearance, RestoreCommand = p.RestoreCommand });
        }
        return new(s.CustomName, s.ProfileName, state.Panes[0].Cwd, s.Ws.Id, s.Ws.Name, s.Flagged,
            s.CustomName ?? s.Name, s.Name, s.Id, Recent.NextSequence(), s.Context, state, Id, Guid.NewGuid().ToString());
    }
    private void CaptureClosedSession(Ses s)
    {
        if (_isQuickWindow) return;
        try { _closedSessions.Add(CaptureClosedSessionData(s)); SaveRecent(); }
        catch (Exception e) { ShowToast("Recent capture failed: " + e.Message); }
    }
    private void CaptureClosedWorkspace(Workspace ws, IReadOnlyList<Ses> sessions)
    {
        if (_isQuickWindow) return;
        try
        {
            _closedWorkspaces.Add(new(ws.Name, sessions.Select(CaptureClosedSessionData).ToList(), Recent.NextSequence(),
                ws.Id, Id, Guid.NewGuid().ToString(), ws.Expanded, DefaultDir: ws.DefaultDir));
            SaveRecent();
        }
        catch (Exception e) { ShowToast("Recent capture failed: " + e.Message); }
    }

    public object RecentList()
    {
        RefreshRecent();
        return LiveWindow().InvokeOnUiQueued<object>(() => new
        {
            entries = _closedSessions.Select(s => new { id = s.EntryId, kind = "session", name = s.Display,
                sourceWindowId = s.SourceWindowId, sessionCount = 1, sequence = s.Seq, destinationWindowId = (string?)null, destinationWorkspaceId = (string?)null })
                .Concat(_closedWorkspaces.Select(w => new { id = w.EntryId, kind = "workspace", name = w.Name,
                    sourceWindowId = w.SourceWindowId, sessionCount = w.Sessions.Count, sequence = w.Seq,
                    destinationWindowId = w.DestinationWindowId, destinationWorkspaceId = w.DestinationWorkspaceId }))
                .OrderByDescending(e => e.sequence).ToArray(), warning = Recent.Warning
        });
    }

    public object RecentClear(string? entryId)
    {
        RefreshRecent();
        return LiveWindow().InvokeOnUiQueued(() => ClearRecentCore(entryId));
    }
    private object ClearRecentCore(string? entryId)
    {
        int before = _closedSessions.Count + _closedWorkspaces.Count;
        if (entryId is null) { _closedSessions.Clear(); _closedWorkspaces.Clear(); }
        else if (_closedSessions.RemoveAll(s => s.EntryId == entryId) + _closedWorkspaces.RemoveAll(w => w.EntryId == entryId) == 0)
            throw new ArgumentException("recent entry not found");
        int removed = before - _closedSessions.Count - _closedWorkspaces.Count;
        SaveRecent(clearAll: entryId is null); return new { cleared = removed, warning = Recent.Warning };
    }
    public object RecentOpen(string entryId, string? window)
    {
        RefreshRecent();
        return LiveWindow().InvokeOnUiQueued(() =>
        {
            Program destination;
            if (window is null or "active") destination = Frontmost;
            else
            {
                var matches = _byId.Values.Where(w => !w._uiGone.IsCancellationRequested &&
                    (w.Id == window || w.Id.StartsWith(window, StringComparison.Ordinal))).ToArray();
                destination = matches.FirstOrDefault(w => w.Id == window) ??
                    (matches.Length == 1 ? matches[0] : throw new ArgumentException("unknown or ambiguous destination window"));
            }
            if (destination._isQuickWindow || destination._uiGone.IsCancellationRequested) throw new ArgumentException("destination must be an open library window");
            return destination.OpenRecentCore(entryId);
        });
    }

    private sealed record RecentMember(string originalId, string? sessionId, string[] paneIds, string? error);
    private object OpenRecentCore(string entryId)
    {
        var single = _closedSessions.FirstOrDefault(s => s.EntryId == entryId);
        var group = _closedWorkspaces.FirstOrDefault(w => w.EntryId == entryId);
        if (single is null && group is null) throw new ArgumentException("recent entry not found (use exact entry ID)");
        Workspace ws;
        if (group is not null)
        {
            if (group.DestinationWindowId is not null)
            {
                if (group.DestinationWindowId != Id) throw new ArgumentException("partial workspace must be retried in its original destination window");
                ws = _workspaces.FirstOrDefault(w => w.Id == group.DestinationWorkspaceId)
                    ?? throw new ArgumentException("partial destination workspace no longer exists; clear the entry explicitly");
            }
            else
            {
                ws = CreateWorkspace(RecentFreeId(group.WorkspaceId), group.Name);
                ws.DefaultDir = group.DefaultDir;
            }
        }
        else ws = _workspaces.FirstOrDefault(w => w.Id == single!.WorkspaceId) ?? ActiveWorkspace();
        var members = new List<RecentMember>();
        var pending = new List<ClosedSession>();
        Ses? first = null;
        foreach (var item in group?.Sessions ?? [single!])
        {
            try
            {
                var s = RestoreRecentSession(item, ws); first ??= s;
                members.Add(new(item.SessionId, s.Id, s.Panes.Select(p => p.Id).ToArray(), null));
            }
            catch (Exception e) { pending.Add(item); members.Add(new(item.SessionId, null, [], e.Message)); }
        }
        if (group is not null)
        {
            _closedWorkspaces.Remove(group);
            if (pending.Count > 0) _closedWorkspaces.Add(group with { Sessions = pending, DestinationWindowId = Id, DestinationWorkspaceId = ws.Id });
            ws.Expanded = group.Expanded;
        }
        else if (pending.Count == 0) _closedSessions.Remove(single!);
        if (first is not null) SetActive(first);
        else if (group is not null && pending.Count == 0) SelectWorkspaceCore(ws);
        SaveRecent(); SaveState(); RequestRedraw();
        if (single is not null && pending.Count > 0) throw new InvalidOperationException(members[0].error);
        return new { entryId, outcome = pending.Count == 0 ? "completed" : "partial", windowId = Id, workspaceId = ws.Id,
            members, remaining = pending.Count, warning = Recent.Warning };
    }

    private static string RecentFreeId(string wanted)
    {
        bool used = _byId.Values.Any(w => w._workspaces.Any(ws => ws.Id == wanted || ws.Sessions.Any(s =>
            s.Id == wanted || s.AllPanes.Any(p => p.Id == wanted))));
        return used ? Guid.NewGuid().ToString() : wanted;
    }
    private Ses RestoreRecentSession(ClosedSession item, Workspace ws)
    {
        var state = item.State;
        var profile = ResolveProfile(state.Profile);
        if (state.Profile is not null && !_profileCfg.Profiles.Any(p => p.Name.Equals(state.Profile, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("saved profile is unavailable: " + state.Profile);
        if (profile?.Elevate == true && !IsElevated()) throw new ArgumentException("elevated profile requires an elevated destination");
        foreach (var pane in state.Panes)
            if (pane.Cwd.Length > 0 && !WslIntegration.IsWsl(profile?.Command) && !Directory.Exists(pane.Cwd))
                throw new ArgumentException("saved directory is unavailable: " + pane.Cwd);
        string id = RecentFreeId(state.Id);
        string PaneId(PaneState p) => p.Id == state.Id ? id : RecentFreeId(p.Id);
        Ses? s = null;
        var oldActive = _active;
        try
        {
            var p = state.Panes[0];
            s = CreateSession(id, state.Name, p.Cwd, ws, false,
                PaneFontSize.Restore(p.FontSize, (float)_config.FontSize, p.FontZoomed).Size,
                profileName: state.Profile, paneId: PaneId(p));
            s.Axis = RestoreState.LoadAxis(state.Axis);
            if (state.Panes.Count > 1)
            {
                p = state.Panes[1];
                AppendPane(s, PaneId(p), p.Cwd, PaneFontSize.Restore(p.FontSize, (float)_config.FontSize, p.FontZoomed).Size);
            }
            for (int i = 0; i < state.Panes.Count; i++)
            {
                s.Panes[i].Ratio = state.Panes[i].Ratio;
                s.Panes[i].FontZoomed = PaneFontSize.Restore(state.Panes[i].FontSize, (float)_config.FontSize, state.Panes[i].FontZoomed).Zoomed;
                s.Panes[i].RestoreCommand = state.Panes[i].RestoreCommand;
                ApplyPaneAppearance(s.Panes[i], PaneAppearance.Restore(state.Panes[i].Appearance));
            }
            s.Active = Math.Clamp(state.Active, 0, s.Panes.Count - 1);
            s.CustomName = state.CustomName; s.Flagged = state.Flagged; s.Context = RestoreState.LoadContext(state.Context);
            if (state.SplitHidden == true) CollapseToSinglePane(s);
            RegridSession(s); ObserveLayout(s); return s;
        }
        catch
        {
            if (s is not null) { DisposeSessionResources(s); lock (_workspaces) ws.Sessions.Remove(s); }
            if (oldActive is not null && OwnsSession(oldActive)) SetActive(oldActive);
            throw;
        }
    }
    private void ReopenMostRecent()
    {
        long a = _closedSessions.LastOrDefault()?.Seq ?? -1, b = _closedWorkspaces.LastOrDefault()?.Seq ?? -1;
        if (a < 0 && b < 0) { ShowToast("No recently closed items"); return; }
        if (b > a) ReopenClosedWorkspace(_closedWorkspaces[^1]); else ReopenClosedSession();
    }
    private void ReopenClosedSession(ClosedSession? which = null) => ReopenRecentFromMenu((which ?? _closedSessions.LastOrDefault())?.EntryId);
    private void ReopenClosedWorkspace(ClosedWorkspace which) => ReopenRecentFromMenu(which.EntryId);
    private void ReopenRecentFromMenu(string? id)
    {
        if (id is null) { ShowToast("No recently closed items"); return; }
        try { OpenRecentCore(id); if (_closedSessions.Any(s => s.EntryId == id) || _closedWorkspaces.Any(w => w.EntryId == id)) ShowToast("Some recent work could not reopen; inspect recent list/open for details"); }
        catch (Exception e) { ShowToast(e.Message); }
    }
}
