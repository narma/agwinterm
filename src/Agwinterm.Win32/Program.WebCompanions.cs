using Agwinterm.Pty;

namespace Agwinterm.Win32;

internal partial class Program
{
    private Ses CreatePageCompanion(Ses parent, WebOverlayRequest request)
    {
        if (!OwnsSession(parent)) throw new ArgumentException("companion parent no longer exists");
        if (parent.PageParentId is { } parentId) parent = FindSessionByExactId(parentId) ?? throw new ArgumentException("companion parent no longer exists");
        string id = Guid.NewGuid().ToString();
        string name = request.Html is { } file ? Path.GetFileName(file) : new Uri(request.Url!).GetLeftPart(UriPartial.Authority);
        var session = new Ses { Id = id, Name = name, Ws = parent.Ws, PageParentId = parent.Id, ProfileName = parent.ProfileName };
        // Compatibility surface for native shell layout/status only. Never StartAsync:
        // no ConPTY, child process, host session, shell hooks or startup command exists.
        session.Panes.Add(new Pane { Id = id, S = new TerminalSession(1, 1), FontSize = (float)_config.FontSize, StartCwd = CwdOf(parent.ActivePane) });
        lock (_workspaces) parent.Ws.Sessions.Insert(parent.Ws.Sessions.IndexOf(parent) + 1, session);
        RequestRedraw(); EmitEvent("tree");
        return session;
    }

    public bool IsPageCompanion(string? target) => InvokeOnUiQueued(() => FindSesForTarget(target)?.PageParentId is not null);
}
