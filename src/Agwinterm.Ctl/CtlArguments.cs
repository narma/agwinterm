using System.Globalization;

namespace Agwinterm.Ctl;

/// <summary>Validate the CLI vocabulary before any installer or pipe side effect.</summary>
public static class CtlArguments
{
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    { "json", "spinner", "stdin", "clear", "allow-control", "all", "styles", "wait", "block",
      "no-select", "create-workspace", "blink", "auto-reset", "next", "prev", "close", "remove", "persist", "auto-size", "watch", "full", "follow", "sticky", "no-frame", "markdown", "collapsed" };

    // Per operation options, in addition to the transport and output controls.
    private static readonly Dictionary<string, string> Vocabulary = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ping"] = "", ["version"] = "", ["tree"] = "window",
        ["recent list"] = "", ["recent open"] = "window", ["recent clear"] = "",
        ["events"] = "window since limit kind run watch",
        ["session wait"] = "window target scope wait-timeout",
        ["install hooks"] = "window target", ["install skill"] = "window target",
        ["install shell"] = "window target", ["install cli"] = "window target remove",
        ["omp list"] = "", ["omp set"] = "window persist",
        ["workspace new"] = "window name collapsed", ["workspace rename"] = "window target",
        ["workspace delete"] = "window target", ["workspace select"] = "window target",
        ["workspace go"] = "window to", ["workspace collapse"] = "window target",
        ["workspace expand"] = "window target", ["workspace focus"] = "window target", ["workspace filter"] = "window", ["workspace flag"] = "window target",
        ["workspace move"] = "window target to",
        ["session new"] = "window command command-mode cwd name workspace workspace-name create-workspace profile no-select wait before after",
        ["session select"] = "window target", ["session close"] = "window target targets",
        ["session duplicate"] = "window target", ["session rename"] = "window target name clear",
        ["session context"] = "window target clear stdin", ["session metrics"] = "window target",
        ["session status"] = "window target blink auto-reset sound",
        ["session type"] = "window target stdin allow-control select",
        ["session write"] = "window target stdin select", ["session text"] = "window target styles all lines",
        ["session copy"] = "window target", ["session seen"] = "window target", ["session output"] = "window target",
        ["session paste"] = "window target text", ["session go"] = "window",
        ["session move"] = "window target to targets before after", ["session search"] = "window target close next prev",
        ["session split"] = "window target axis", ["session split close"] = "window target",
        ["session swap"] = "window target", ["session readonly"] = "window target",
        ["session scratch"] = "window target", ["session focus"] = "window target",
        ["session flag"] = "window target", ["session bind"] = "window target resume pid cwd",
        ["session restore"] = "window target command", ["session switch"] = "window",
        ["session resize"] = "window target split-ratio grow-left grow-right grow-top grow-bottom",
        ["session background set"] = "window target path opacity mode",
        ["session background clear"] = "window target",
        ["session overlay open"] = "window target command pane wait block size-percent width height cwd follow background-color",
        ["session overlay close"] = "window target pane", ["session overlay result"] = "window target pane",
        ["session overlay copy"] = "window target pane", ["session overlay text"] = "window target pane all lines",
        ["session overlay resize"] = "window target size-percent width height full pane",
        ["session overlay hide"] = "window target pane", ["session overlay show"] = "window target pane", ["session overlay toggle"] = "window target pane",
        ["session hud open"] = "window target detail spinner spinner-style position size-percent background-color text-color hide-after font-size sticky no-frame markdown pane file",
        ["session hud update"] = "window target detail spinner spinner-style position size-percent background-color text-color hide-after font-size sticky no-frame markdown pane file",
        ["session hud close"] = "window target",
        ["command run"] = "window target command mode", ["command list"] = "window", ["command leader"] = "window",
        ["claude adopt"] = "window", ["claude yolo"] = "window target", ["claude update"] = "window",
        ["app update"] = "window", ["theme list"] = "window", ["theme set"] = "window",
        ["keymap reload"] = "window", ["profiles list"] = "window", ["profiles reload"] = "window",
        ["restore clear"] = "window", ["restore capture"] = "window target",
        ["config get"] = "window", ["config set"] = "window", ["config list"] = "window", ["settings"] = "window",
        ["surface cursor"] = "window target", ["selection all"] = "window target", ["selection copy"] = "window target",
        ["selection clear"] = "window target", ["selection finalize"] = "window target",
        ["sidebar"] = "window", ["quick"] = "window", ["broadcast"] = "window",
        ["notify"] = "window target title category", ["font inc"] = "window target", ["font dec"] = "window target",
        ["font reset"] = "window target", ["dashboard"] = "window close font-size auto-size",
        ["window new"] = "name", ["window list"] = "", ["window state"] = "",
        ["window select"] = "target", ["window close"] = "target", ["window delete"] = "target",
        ["window zoom"] = "target", ["window rename"] = "", ["window resize"] = "w h", ["window move"] = "x y",
        ["image show"] = "window target row col id", ["image sixel"] = "window target row col",
        ["image frameshm"] = "window target images " + string.Join(' ', FrameShmCli.NumericFields),
    };
    private static readonly HashSet<string> Integers = new(StringComparer.OrdinalIgnoreCase)
    { "since", "limit", "pid", "size-percent", "opacity", "font-size", "grow-left", "grow-right", "grow-top", "grow-bottom", "w", "h", "x", "y" };
    private static readonly HashSet<string> Known = new(Vocabulary.Values.SelectMany(v => v.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Concat(new[] { "pipe", "socket", "timeout", "json" }), StringComparer.OrdinalIgnoreCase);

    public static bool TryParse(string[] argv, out List<string> positionals, out Dictionary<string, string> options,
        out HashSet<string> valued, out HashSet<string> bare, out bool json, out string? error)
    {
        positionals = []; options = new(StringComparer.OrdinalIgnoreCase);
        valued = new(StringComparer.OrdinalIgnoreCase); bare = new(StringComparer.OrdinalIgnoreCase);
        json = false; error = null;
        for (int i = 0; i < argv.Length; i++)
        {
            string word = argv[i];
            if (!word.StartsWith("--")) { positionals.Add(word); continue; }
            string key = word[2..].ToLowerInvariant();
            if (!Known.Contains(key)) { error = $"unknown option --{key}. Nothing sent."; return false; }
            if (options.ContainsKey(key) || (key is "pipe" or "socket" && (options.ContainsKey("pipe") || options.ContainsKey("socket"))))
            { error = $"duplicate option --{key}; selectors are single-valued. Nothing sent."; return false; }
            bool takes = !Flags.Contains(key) && i + 1 < argv.Length && !argv[i + 1].StartsWith("--");
            if (!takes && !Flags.Contains(key) && key != "sound")
            { error = key == "pane" ? "--pane needs a word: left or right. Nothing sent." : $"--{key} needs a value. Nothing sent."; return false; }
            string value = takes ? argv[++i] : "true";
            options.Add(key, value);
            if (takes) valued.Add(key); else bare.Add(key);
            if (key is "target" or "window" or "pipe" or "socket" && string.IsNullOrWhiteSpace(value))
            { error = $"--{key} is empty. Nothing sent."; return false; }
            if (key == "json") json = true;
            if (key == "timeout" && !CtlRequest.TryResolveTimeout(value, false, out _, out error)) return false;
            if (key == "split-ratio" && (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n)))
            { error = "--split-ratio needs a finite number. Nothing sent."; return false; }
        }
        string area = positionals.ElementAtOrDefault(0)?.ToLowerInvariant() ?? "";
        string sub = positionals.ElementAtOrDefault(1)?.ToLowerInvariant() ?? "";
        string operation = area is "ping" or "version" or "tree" or "events" or "settings" or "sidebar" or "quick" or "broadcast" or "notify" or "dashboard"
            ? area : area + " " + sub;
        string third = positionals.ElementAtOrDefault(2)?.ToLowerInvariant() ?? "";
        if (operation is "session overlay" or "session background") operation += " " + (third.Length == 0 ? sub == "overlay" ? "open" : "set" : third);
        // HUD action words are case-sensitive in the request builder; uppercase words
        // such as CLOSE remain literal messages and may use open's display options.
        if (operation == "session hud") operation += " " + (positionals.ElementAtOrDefault(2) is "open" or "update" or "close" ? third : "open");
        if (operation == "session split" && third == "close") operation += " close";
        if (!Vocabulary.TryGetValue(operation, out string? vocabulary))
        { error = $"unknown command '{operation}'. Nothing sent."; return false; }
        var allowed = new HashSet<string>(vocabulary.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase)
            { "pipe", "socket", "timeout", "json" };
        foreach (string key in options.Keys)
            if (!allowed.Contains(key))
            {
                // Preserve the specialized text validator's established diagnostic.
                if (operation == "session overlay text" && key == "styles") continue;
                error = $"{operation}: unsupported option --{key}. Nothing sent."; return false;
            }
        options.Remove("json"); valued.Remove("json"); bare.Remove("json");
        // Command-specific number rules need the parsed command, independent of option order.
        foreach (var (key, value) in options)
        {
            bool hudFont = key == "font-size" && operation is "session hud open" or "session hud update";
            if (!hudFont && (Integers.Contains(key) || FrameShmCli.NumericFields.Contains(key)) &&
                !(key is "seq" or "since" ? long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) :
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            { error = $"--{key} needs a whole number. Nothing sent."; return false; }
        }
        // Positionals and options must not specify two different sources for the same value.
        bool positionalTarget = operation is "session select" or "session close" or "session duplicate" or "session metrics"
            or "workspace collapse" or "workspace expand" or "window select" or "window close" or "window delete" or "window zoom";
        if (positionalTarget && positionals.Count > 2 && options.ContainsKey("target"))
        { error = "choose a positional target or --target, not both. Nothing sent."; return false; }
        if (operation == "session new" && options.ContainsKey("workspace") && options.ContainsKey("workspace-name"))
        { error = "choose --workspace or --workspace-name. Nothing sent."; return false; }
        if (operation == "command run" && options.TryGetValue("mode", out string? mode) && mode.ToLowerInvariant() is not ("send" or "new" or "overlay" or "detached"))
        { error = "command run: mode must be send, new, overlay or detached. Nothing sent."; return false; }
        if (operation == "command run" && positionals.Count > 2 && options.ContainsKey("command"))
        { error = "command run: choose positional text or --command. Nothing sent."; return false; }
        if (operation is "window resize" or "window move")
        {
            string[] fields = operation == "window resize" ? ["w", "h"] : ["x", "y"];
            for (int i = 0; i < fields.Length; i++)
            {
                if (positionals.Count > i + 3 && (options.ContainsKey(fields[i]) ||
                    !int.TryParse(positionals[i + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
                { error = $"{operation}: invalid or duplicate {fields[i]}. Nothing sent."; return false; }
            }
        }
        if (operation == "sidebar" && sub == "width" && positionals.Count > 2 &&
            !int.TryParse(positionals[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        { error = "sidebar width needs a whole number. Nothing sent."; return false; }
        return true;
    }
}
