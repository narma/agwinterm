using Agwinterm.Core;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

internal partial class Program
{
    private sealed record AgentRow(Ses Session, Pane Pane, string RecordId, string Name,
        string Detail, AgentStatus Status, long ChangedAt, bool Pinned, string? Group = null)
    {
        public string Key => Pane.Id + "/" + RecordId;
    }
    private readonly HashSet<string> _agentPins = new(StringComparer.Ordinal);
    private readonly HashSet<string> _foldedAgents = new(StringComparer.Ordinal);
    private readonly List<(float Top, float Bottom, AgentRow Row)> _agentRows = new();
    private string _agentSort = "priority";
    private bool _agentsCollapsed;
    private int _agentsScroll, _agentsMaxScroll;
    private float _agentsTop = float.MaxValue;
    private bool _agentsKeyboardFocus;
    private string? _agentKeyboardKey;
    private readonly object _agentSortMarker = new();
    private readonly object _agentHeadingMarker = new();

    private bool AgentIsViewed(Pane pane) => GetForegroundWindow() == _hwnd &&
        ReferenceEquals(ActiveSurface(), pane) && !_dashboardOpen;

    private List<AgentRow> AgentList()
    {
        var result = new List<AgentRow>();
        foreach (var ses in AllSessions())
        foreach (var pane in ses.AllPanes)
        {
            bool viewed = AgentIsViewed(pane);
            // Explicit idle reports also identify agents; untouched ordinary shells do not.
            if (!pane.AgentActivity.Reported && pane.S.StatusSignal?.Snapshot.Version > pane.LastProgramStatusVersion)
                pane.AgentActivity.Observe(pane.S.Status, viewed);
            var records = pane.ProgramStatuses.Records.ToList();
            if (records.Count == 0 && (pane.AgentActivity.Reported || pane.AgentResume is not null ||
                _agentPins.Contains(pane.Id + "/")))
                result.Add(new(ses, pane, "", ses.SidebarName, ses.Ws.Name,
                    pane.AgentActivity.Display(viewed), pane.AgentActivity.ChangedAt, _agentPins.Contains(pane.Id + "/")));
            foreach (var record in records)
            {
                if (!pane.ProgramActivities.TryGetValue(record.Id, out var activity)) continue;
                string name = record.Title.Length > 0 ? record.Title : record.Id.Length > 0 ? record.Id : ses.SidebarName;
                string app = record.App;
                string parent = record.Id;
                while (app.Length == 0 && parent.Length > 0)
                {
                    int slash = parent.LastIndexOf('/'); parent = slash < 0 ? "" : parent[..slash];
                    app = records.FirstOrDefault(r => r.Id == parent)?.App ?? "";
                }
                string detail = string.Join(" · ", new[] { ses.Ws.Name, app, record.Kind, record.Message }.Where(s => !string.IsNullOrEmpty(s)));
                if (record.Progress is int progress) detail += $" · {progress}%";
                result.Add(new(ses, pane, record.Id, name, detail, activity.Display(viewed), activity.ChangedAt,
                    _agentPins.Contains(pane.Id + "/" + record.Id)));
            }
        }
        var roots = result.Where(r => r.RecordId.Length == 0).ToDictionary(r => r.Pane);
        var byPane = result.ToLookup(r => r.Pane);
        var top = result.Where(r => r.RecordId.Length == 0 || r.Pinned || !roots.ContainsKey(r.Pane))
            .Select(r => r.RecordId.Length == 0 ? r with { Status = byPane[r.Pane]
                .Select(c => c.Status).OrderBy(AgentActivity.Priority).First() } : r);
        var ordered = top.OrderByDescending(r => r.Pinned)
            .ThenBy(r => _agentSort == "grouped" && !r.Pinned ? r.Session.Ws.Name : "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => AgentActivity.Priority(r.Status)).ThenByDescending(r => r.ChangedAt)
            .ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
        var rows = new List<AgentRow>();
        foreach (var root in ordered)
        {
            string group = root.Pinned ? "Pinned" : _agentSort == "grouped" ? root.Session.Ws.Name : AgentStatusName(root.Status);
            rows.Add(root with { Group = group });
            if (root.RecordId.Length == 0 && !_foldedAgents.Contains(root.Pane.Id))
                rows.AddRange(byPane[root.Pane].Where(c => c.RecordId.Length > 0 && !c.Pinned)
                    .OrderBy(c => c.RecordId, StringComparer.Ordinal).Select(c => c with { Group = group }));
        }
        return rows;
    }

    private float PrepareAgents(float bottom, float rowH)
    {
        _agentRows.Clear();
        float available = MathF.Max(0, bottom - TitleBarH);
        float height = _agentsCollapsed ? rowH : MathF.Min(available * .45f, MathF.Max(rowH * 4, available * .4f));
        _agentsTop = MathF.Max(TitleBarH, bottom - height);
        return _agentsTop;
    }

    private void DrawAgents(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, float rowH, float bottom)
    {
        float y = _agentsTop;
        brush.Color = ChromeDim;
        rt.DrawLine(new(8, y), new(_sidebarW - 8, y), brush, .5f);
        brush.Color = SbHeaderText;
        var agents = AgentList();
        rt.DrawText(($"{(_agentsCollapsed ? "▸" : "▾")}  Agents  {agents.Count}"), _sidebarFont,
            new Rect(12, y, MathF.Max(0, _sidebarW - 62), rowH), brush, DrawTextOptions.Clip);
        rt.DrawText("≡", _sidebarFont, new Rect(_sidebarW - 32, y, 26, rowH), brush);
        y += rowH;
        if (_agentsCollapsed) return;
        if (agents.Count == 0)
        {
            brush.Color = SbDimText;
            rt.DrawText("No agents yet", _sidebarSmall, new Rect(16, y, _sidebarW - 24, rowH), brush);
            return;
        }
        // Each group heading and row is one scroll slot. This keeps all agents reachable
        // in a short window without stealing wheel input from the terminal.
        var slots = new List<(string? Heading, AgentRow? Row)>();
        string? last = null;
        foreach (var a in agents)
        {
            string group = a.Group!;
            if (group != last) { slots.Add((group, null)); last = group; }
            slots.Add((null, a));
        }
        int capacity = Math.Max(1, (int)((bottom - y) / rowH));
        _agentsMaxScroll = Math.Max(0, slots.Count - capacity);
        _agentsScroll = Math.Clamp(_agentsScroll, 0, _agentsMaxScroll);
        foreach (var slot in slots.Skip(_agentsScroll).Take(capacity))
        {
            if (slot.Row is not { } a)
            {
                brush.Color = SbDimText;
                rt.DrawText(slot.Heading!, _sidebarSmall, new Rect(16, y, _sidebarW - 24, rowH), brush, DrawTextOptions.Clip);
            }
            else
            {
                if (ReferenceEquals(_active?.ActivePane, a.Pane))
                {
                    brush.Color = SbHighlight;
                    rt.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(6, y + 1, _sidebarW - 12, rowH - 2), RadiusX = 5, RadiusY = 5 }, brush);
                }
                float indent = a.RecordId.Length == 0 ? 0 : Math.Min(3, a.RecordId.Count(c => c == '/') + 1) * 8;
                brush.Color = StatusDot(a.Status);
                var dot = new Ellipse(new(16 + indent, y + rowH / 2), 3.5f, 3.5f);
                if (a.Status == AgentStatus.Idle) rt.DrawEllipse(dot, brush, 1.2f); else rt.FillEllipse(dot, brush);
                float x = 27 + indent;
                brush.Color = SbActiveText;
                rt.DrawText(a.Name, _sidebarFont, new Rect(x, y, MathF.Max(0, _sidebarW - x - 40), rowH), brush, DrawTextOptions.Clip);
                float suffixX = x + MeasureText(a.Name, _sidebarFont) + 6;
                if (_sidebarW - suffixX - 40 > 24)
                {
                    brush.Color = SbDimText;
                    rt.DrawText("· " + a.Session.Ws.Name, _sidebarCtx, new Rect(suffixX, y, _sidebarW - suffixX - 40, rowH), brush, DrawTextOptions.Clip);
                }
                brush.Color = SbDimText;
                long minutes = Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - a.ChangedAt) / 60);
                string age = minutes < 1 ? "<1m" : minutes < 60 ? $"{minutes}m" : minutes < 1440 ? $"{minutes / 60}h" : $"{minutes / 1440}d";
                rt.DrawText(age, _sidebarSmall, new Rect(_sidebarW - 38, y, 34, rowH), brush, DrawTextOptions.Clip);
                _agentRows.Add((y, y + rowH, a));
                if (_chromeFocus && _agentsKeyboardFocus && _agentKeyboardKey == a.Key)
                {
                    brush.Color = ChromeAccent;
                    rt.DrawRectangle(new Rect(7, y + 2, _sidebarW - 14, rowH - 4), brush, 1.5f);
                }
                _sidebarNames.Add(new SidebarNameHit(a, $"{a.Name} · {AgentStatusName(a.Status)} · {a.Detail}", 6, y, _sidebarW - 12, rowH));
            }
            y += rowH;
        }
        if (_agentsMaxScroll > 0)
        {
            brush.Color = ChromeDim;
            float track = bottom - _agentsTop - rowH, thumb = MathF.Max(12, track * capacity / slots.Count);
            rt.FillRectangle(new Rect(_sidebarW - 3, _agentsTop + rowH + (track - thumb) * _agentsScroll / _agentsMaxScroll, 2, thumb), brush);
        }
    }

    private static string AgentStatusName(AgentStatus status) => status switch
    { AgentStatus.Active => "Working", AgentStatus.Blocked => "Blocked", AgentStatus.Completed => "Done", _ => "Idle" };

    private object? AgentAt(int x, int y)
    {
        if (x >= _sidebarW || y < _agentsTop || y >= ClientH() - FooterH) return null;
        foreach (var row in _agentRows) if (y >= row.Top && y < row.Bottom) return row.Row;
        float rowH = MathF.Max(_cellH + 8, MathF.Ceiling(Math.Clamp(_config.SidebarFontSize, 9, 20) * 1.5f) + 6);
        return y < _agentsTop + rowH ? x >= _sidebarW - 38 ? _agentSortMarker : _agentHeadingMarker : null;
    }

    private void ActivateAgent(AgentRow a)
    {
        if (!OwnsSession(a.Session) || !a.Session.AllPanes.Contains(a.Pane)) return;
        if (ReferenceEquals(a.Session.HiddenSplit, a.Pane)) SplitPane(a.Session);
        a.Session.Active = a.Session.Panes.IndexOf(a.Pane);
        SetActive(a.Session);
        RequestRedraw();
    }

    private bool AgentClick(int x, int y)
    {
        if (y < _agentsTop || y >= ClientH() - FooterH) return false;
        var item = AgentAt(x, y);
        if (item is AgentRow row) ActivateAgent(row);
        else if (ReferenceEquals(item, _agentHeadingMarker))
        { _agentsCollapsed = !_agentsCollapsed; RequestRedraw(); SaveState(); }
        else if (ReferenceEquals(item, _agentSortMarker))
        { GetCursorScreen(out int sx, out int sy); ShowMenuWindow(AgentMenu(item), sx, sy); }
        return true;
    }

    private List<PalItem> AgentMenu(object item)
    {
        var items = new List<PalItem>();
        void Add(string label, Action action) => items.Add(new() { Label = label, Search = label, Run = action });
        if (item is AgentRow a)
        {
            Add("Open agent", () => ActivateAgent(a));
            Add(a.Pinned ? "Unpin agent" : "Pin agent", () => {
                if (!_agentPins.Remove(a.Key)) _agentPins.Add(a.Key);
                SaveState(); RequestRedraw();
            });
            if (a.RecordId.Length == 0 && a.Pane.ProgramStatuses.Records.Any(r => r.Id.Length > 0))
                Add(_foldedAgents.Contains(a.Pane.Id) ? "Expand workers" : "Collapse workers", () => {
                    if (!_foldedAgents.Remove(a.Pane.Id)) _foldedAgents.Add(a.Pane.Id);
                    RequestRedraw();
                });
        }
        Add((_agentSort == "priority" ? "✓ " : "") + "Priority", () => SetAgentSort("priority"));
        Add((_agentSort == "grouped" ? "✓ " : "") + "Grouped by workspace", () => SetAgentSort("grouped"));
        return items;
    }
    private void SetAgentSort(string sort)
    { _agentSort = sort; _agentsScroll = 0; SaveState(); RequestRedraw(); }

    private bool AgentsKey(int vk)
    {
        var rows = AgentList();
        int index = Math.Max(0, rows.FindIndex(r => r.Key == _agentKeyboardKey));
        if (vk is VK_ESCAPE or 0x75) { _agentsKeyboardFocus = false; ExitChromeFocus(); return true; }
        if (rows.Count == 0) return true;
        if (vk == VK_DOWN) index = Math.Min(rows.Count - 1, index + 1);
        else if (vk == VK_UP) index = Math.Max(0, index - 1);
        else if (vk == VK_HOME) index = 0;
        else if (vk == VK_END) index = rows.Count - 1;
        else if (vk is VK_RETURN or VK_SPACE)
        { ActivateAgent(rows[index]); _agentsKeyboardFocus = false; ExitChromeFocus(); return true; }
        else if (vk == 0x50) // P: pin/unpin without opening a menu.
        {
            if (!_agentPins.Remove(rows[index].Key)) _agentPins.Add(rows[index].Key);
            _agentKeyboardKey = rows[index].Key;
            SaveState(); return AgentsKey(0);
        }
        else if (vk == 0x53) { ChromeAction("agents-sort"); return true; }
        _agentKeyboardKey = rows[index].Key;
        int slot = 0; string? group = null;
        foreach (var row in rows)
        {
            if (row.Group != group) { slot++; group = row.Group; }
            if (row.Key == _agentKeyboardKey) break;
            slot++;
        }
        float rowH = MathF.Max(_cellH + 8, MathF.Ceiling(Math.Clamp(_config.SidebarFontSize, 9, 20) * 1.5f) + 6);
        int capacity = Math.Max(1, (int)((ClientH() - FooterH - _agentsTop - rowH) / rowH));
        if (slot < _agentsScroll) _agentsScroll = slot;
        else if (slot >= _agentsScroll + capacity) _agentsScroll = slot - capacity + 1;
        _uia.Announce($"{rows[index].Name}, {AgentStatusName(rows[index].Status)}");
        RequestRedraw(); return true;
    }
}
