using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

internal partial class Program : INativeWindowHost
{
    private bool _startMinimized;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorDetails
    {
        public int size; public RECT bounds, work; public uint flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string device;
    }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref RECT bounds, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] private static extern bool ReadMonitor(IntPtr monitor, ref MonitorDetails details);
    private sealed record Display(IntPtr Handle, string Id, bool Primary, DesktopRect Bounds, DesktopRect Work);
    private static DesktopRect Rect(RECT r) => new(r.left, r.top, r.right - r.left, r.bottom - r.top);
    private static RECT NativeRect(DesktopRect r) => new() { left = r.X, top = r.Y, right = r.X + r.Width, bottom = r.Y + r.Height };
    private static List<Display> Displays()
    {
        var result = new List<Display>();
        MonitorCallback callback = (IntPtr h, IntPtr dc, ref RECT r, IntPtr data) =>
        {
            var info = new MonitorDetails { size = Marshal.SizeOf<MonitorDetails>() };
            if (!ReadMonitor(h, ref info)) return false;
            result.Add(new(h, info.device, (info.flags & 1) != 0, Rect(info.bounds), Rect(info.work))); return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero) || result.Count == 0) throw new InvalidOperationException("monitor enumeration failed");
        return result;
    }
    private static Program NativeWindow(string? selector)
    {
        lock (_windowIndex)
        {
            if (selector is null or "active") return Frontmost;
            if (_byId.TryGetValue(selector, out var exact)) return exact;
            var matches = _byId.Values.Where(w => w.Id.StartsWith(selector, StringComparison.Ordinal)).ToArray();
            return matches.Length == 1 ? matches[0] : throw new ArgumentException("unknown or ambiguous open library window");
        }
    }
    private object NativeGeometry()
    {
        if (!GetWindowRect(_hwnd, out var r)) throw new InvalidOperationException("cannot read window geometry");
        var monitors = Displays(); var display = monitors.FirstOrDefault(d => d.Handle == MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST)) ?? monitors[0];
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(_hwnd, ref wp)) throw new InvalidOperationException("cannot read restore geometry");
        if (_fullscreen) wp = _fsPrev;
        var restore = Rect(wp.rcNormalPosition);
        restore = restore with { X = restore.X + display.Work.X - display.Bounds.X, Y = restore.Y + display.Work.Y - display.Bounds.Y };
        return new { id = Id, rect = Rect(r), restoreRect = restore, monitor = display.Id, dpi = GetDpiForWindow(_hwnd), minimized = IsIconic(_hwnd), maximized = IsZoomed(_hwnd), fullscreen = _fullscreen };
    }
    public object NativeWindowCommand(string command, JsonElement args, string? target, string? window)
    {
        NativeWindowRequest.Validate(command, args, target, window);
        if (command == "session.reveal")
        {
            var context = LiveWindow().InvokeOnUiQueued(() => NativeWindow(window).RevealContext(target));
            string path = RevealDirectory(context.cwd, context.profile, NativeWindowRequest.Bool(args, "path-only"));
            return new { path, opened = !NativeWindowRequest.Bool(args, "path-only") };
        }
        return LiveWindow().InvokeOnUiQueued(() =>
        {
            if (command == "window.monitors") return (object)Displays().Select(d => new { id = d.Id, primary = d.Primary, bounds = d.Bounds, workArea = d.Work }).ToArray();
            if (command == "window.new")
            {
                bool minimized = NativeWindowRequest.Bool(args, "minimized");
                var m = new WinMeta { Id = Guid.NewGuid().ToString(), Name = NativeWindowRequest.String(args, "name") ?? "", IsOpen = true };
                CascadeGeometry(m);
                var previous = Frontmost; string? previousId = _frontmostId;
                var created = CreateWindowInstance(m, minimized);
                if (minimized) { Frontmost = previous; _frontmostId = previousId; }
                else { Frontmost = created; _frontmostId = created.Id; SetForegroundWindow(created._hwnd); }
                SaveIndex(); created.EmitEvent("tree", info: "window-created"); return created.NativeGeometry();
            }
            var p = NativeWindow(target);
            if (command == "window.geometry") return p.NativeGeometry();
            if (command is "window.next" or "window.previous")
            {
                var open = _windowIndex.Where(m => m.IsOpen && _byId.ContainsKey(m.Id)).Select(m => m.Id).ToList();
                if (open.Count < 2) throw new ArgumentException("no other open window");
                p = _byId[open[(open.IndexOf(p.Id) + (command == "window.next" ? 1 : open.Count - 1)) % open.Count]];
                if (IsIconic(p._hwnd)) ShowWindow(p._hwnd, SW_RESTORE);
                Frontmost = p; _frontmostId = p.Id; SetForegroundWindow(p._hwnd);
            }
            else if (command == "window.minimize") ShowWindow(p._hwnd, 7);
            else if (command == "window.restore") ShowWindow(p._hwnd, SW_SHOWNOACTIVATE);
            else if (command == "window.fullscreen")
            {
                string op = NativeWindowRequest.String(args, "op") ?? "toggle";
                if (op == "toggle" || (op == "on") != p._fullscreen) p.ToggleFullscreen(noActivate: true);
            }
            else if (command == "window.move") p.MoveToMonitor(NativeWindowRequest.String(args, "monitor")!);
            p.SaveState(); p.EmitEvent("tree", info: command); return p.NativeGeometry();
        });
    }
    private void MoveToMonitor(string selector)
    {
        var displays = Displays();
        var destination = displays.FirstOrDefault(d => selector == "primary" ? d.Primary : d.Id.Equals(selector, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("unknown monitor");
        bool full = _fullscreen;
        if (full) ToggleFullscreen(noActivate: true);
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(_hwnd, ref wp)) throw new InvalidOperationException("cannot read window placement");
        var normal = Rect(wp.rcNormalPosition);
        var fitted = WindowPlacement.Fit(normal with { X = destination.Work.X, Y = destination.Work.Y }, destination.Work);
        if (IsIconic(_hwnd) || IsZoomed(_hwnd))
        {
            wp.rcNormalPosition = NativeRect(fitted with { X = fitted.X - destination.Work.X + destination.Bounds.X, Y = fitted.Y - destination.Work.Y + destination.Bounds.Y });
            if (!SetWindowPlacement(_hwnd, ref wp)) throw new InvalidOperationException("cannot move restore placement");
        }
        else if (!SetWindowPos(_hwnd, IntPtr.Zero, fitted.X, fitted.Y, fitted.Width, fitted.Height, SWP_NOZORDER | SWP_NOACTIVATE)) throw new InvalidOperationException("cannot move window");
        if (full) ToggleFullscreen(noActivate: true);
    }
    private (string cwd, ShellProfile? profile) RevealContext(string? target)
    {
        Ses? session; Pane? pane;
        if (target is null or "active") { session = _active; pane = session?.ActivePane; }
        else
        {
            var resolved = FindStrictControlPane(target);
            if (resolved is not { cover: false, ses: { } owner } hit)
                throw new ArgumentException("unknown, ambiguous or auxiliary session/pane");
            session = owner; pane = hit.pane;
        }
        if (session is null || pane is null) throw new ArgumentException("no session directory");
        string cwd = PrettyCwd(SafeCwd(pane));
        if (string.IsNullOrWhiteSpace(cwd)) cwd = pane.StartCwd ?? "";
        return (cwd, IntegrationProfileOnUi(session.Id));
    }
    private static string RevealDirectory(string cwd, ShellProfile? profile, bool pathOnly)
    {
        if (string.IsNullOrWhiteSpace(cwd)) throw new ArgumentException("no directory to reveal");
        string path = profile is not null && WslIntegration.IsWsl(profile.Command) && cwd.StartsWith('/') ? WslIntegration.WindowsPath(profile, cwd) : cwd;
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) throw new IOException("directory not found: " + path);
        if (!pathOnly)
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add("/select,"); start.ArgumentList.Add(path); Process.Start(start);
        }
        return path;
    }
}
