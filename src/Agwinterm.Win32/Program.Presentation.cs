using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Color = Agwinterm.Core.Color;

namespace Agwinterm.Win32;

internal partial class Program : IPresentationHost
{
    private static Color? AppearanceColor(string? value) => value is null ? null : new Color(Convert.ToByte(value.Substring(1,2),16), Convert.ToByte(value.Substring(3,2),16), Convert.ToByte(value.Substring(5,2),16));
    private static void ApplyPaneAppearance(Pane pane, PaneAppearance? value)
    { pane.Appearance = value; pane.DefaultBackground = AppearanceColor(value?.Color); }
    private Pane AppearancePane(string? target)
    {
        if (target is null or "active") return _active?.ActivePane ?? throw new ArgumentException("no active pane");
        return FindStrictControlPane(target) is { cover: false, ses: not null } hit ? hit.pane
            : throw new ArgumentException("unknown, ambiguous or auxiliary session/pane");
    }
    public object PresentationCommand(string command, JsonElement args, string? target) => InvokeOnUiQueued(() =>
    {
        if (_isQuickWindow) throw new ArgumentException("presentation requires a library window");
        var value = PresentationRequest.Validate(command, args, target);
        if (command == "app.version") return ExecutableVersion.Read(_termProgramVersion, AppExePath, AppChannel().ToString());
        if (command == "keymap.list") return new
        {
            bindings = new Dictionary<string,string>(_keymap), leader = _leader, leaderBindings = new Dictionary<string,string>(_leaderBindings),
            reserved = new Dictionary<string,string> { ["f1"] = "app help while alternate screen is active", ["f6"] = "accessibility focus-zone navigation takes precedence" }, diagnostics = _keymapDiag.ToArray()
        };
        if (command == "config.reload")
        {
            var notes = new List<string>(); var changed = ReloadConfigApplying(null, notes);
            return new { changed, diagnostics = notes, effectiveTheme = _theme.Name, followSystem = _config.ThemeFollowSystem };
        }
        var pane = AppearancePane(target);
        string op = PresentationRequest.String(args, "op") ?? "get";
        if (op != "get") { ApplyPaneAppearance(pane, op == "clear" ? null : value); SaveState(); RequestRedraw(); EmitEvent("tree", pane.Id, "appearance"); }
        var owner = AllSessions().First(s => s.Panes.Contains(pane));
        uint dynamic = pane.S.Emulator.DynamicBg;
        var bg = dynamic != 0 ? new Color((byte)(dynamic >> 16), (byte)(dynamic >> 8), (byte)dynamic) : pane.DefaultBackground ?? _theme.DefaultBackground;
        return new { paneId = pane.Id, overrides = pane.Appearance, effectiveBackground = $"#{bg.R:X2}{bg.G:X2}{bg.B:X2}", inheritedImage = pane.Appearance?.HideImage == true ? null : owner.BgPath, effectiveTheme = _theme.Name };
    });
    private void DrawPaneText(Pane pane, float x, float y, float width, float height)
    {
        if (pane.Appearance is not { Text: { Length: > 0 } text, Opacity: > 0 } appearance || _rt is null || _brush is null) return;
        _rt.PushAxisAlignedClip(new Vortice.RawRectF(x,y,x+width,y+height), AntialiasMode.PerPrimitive);
        try
        {
            var fg = C4(_theme.DefaultForeground); _brush.Color = new Color4(fg.R,fg.G,fg.B,appearance.Opacity/100f);
            _rt.DrawText(text, _uiFont, new Rect(x+12,y+12,MathF.Max(1,width-24),MathF.Max(1,height-24)),_brush);
        }
        finally { _rt.PopAxisAlignedClip(); }
    }
    private void DrawSessionStatus(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush, Ses session, float x, float y)
    {
        var status = AggStatus(session);
        var appearance = session.Panes.FirstOrDefault(p => p.S.Status == status)?.Appearance;
        var dot = appearance?.StatusColor is { } color ? HexColor4(color, StatusDot(status)) : StatusDot(status);
        if (AggBlink(session) && !_cursorOn) dot = new Color4(dot.R,dot.G,dot.B,0.22f);
        brush.Color = dot;
        if (appearance?.StatusShape == "square") rt.FillRectangle(new Rect(x-4.5f,y-4.5f,9,9),brush);
        else if (appearance?.StatusShape == "diamond")
        {
            using var geometry = _d2d.CreatePathGeometry(); using var sink = geometry.Open();
            sink.BeginFigure(new System.Numerics.Vector2(x,y-6),FigureBegin.Filled);
            sink.AddLine(new System.Numerics.Vector2(x+6,y));sink.AddLine(new System.Numerics.Vector2(x,y+6));sink.AddLine(new System.Numerics.Vector2(x-6,y));sink.EndFigure(FigureEnd.Closed);sink.Close();rt.FillGeometry(geometry,brush);
        }
        else rt.FillEllipse(new Ellipse(new System.Numerics.Vector2(x,y),4.5f,4.5f),brush);
    }
    private void ConfigDiagnostics(List<string> notes)
    {
        if (!File.Exists(ConfigPath)) { notes.Add("configuration file is missing; defaults loaded"); return; }
        var seen = new HashSet<string>(); int lineNumber = 0;
        foreach (string raw in File.ReadLines(ConfigPath))
        {
            lineNumber++; string line = raw.Trim(); if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('='); if (eq <= 0) { notes.Add($"line {lineNumber}: expected key = value"); continue; }
            string key = line[..eq].Trim().ToLowerInvariant(), value = line[(eq+1)..].Trim();
            if (!seen.Add(key)) notes.Add($"line {lineNumber}: duplicate {key}; last parsed value wins");
            if (ValidateConfigValue(key,value) is { } error) { notes.Add($"line {lineNumber}: {error}"); continue; }
            if (key is "theme" or "theme-light" or "theme-dark" && value.Length > 0 && !_allThemes.Any(t => t.Name.Equals(value,StringComparison.OrdinalIgnoreCase)))
                notes.Add($"line {lineNumber}: unknown theme '{value}'; native theme fallback applies");
            string effective = ConfigValue(key);
            if (!string.Equals(value,effective,StringComparison.OrdinalIgnoreCase)) notes.Add($"line {lineNumber}: {key} effective value is '{effective}' (input normalized or rejected by parser)");
        }
    }
}
