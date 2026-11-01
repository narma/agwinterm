using System.Text.Json;

namespace Agwinterm.Pty;

public static class WorkingSetRequest
{
    public static bool Applies(string cmd, JsonElement args) => cmd is "workspace.flag" or "workspace.filter" or "workspace.focus" or "sidebar.flagged-layout" ||
        args.ValueKind == JsonValueKind.Object && (cmd == "workspace.new" && args.TryGetProperty("collapsed", out _) ||
        cmd is "session.new" or "session.move" or "session.close" && args.EnumerateObject().Any(p => p.Name is "before" or "after" or "targets" or "undoable"));
    public static string? String(JsonElement args, string key) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) ? v.GetString() : null;
    public static bool Bool(JsonElement args, string key) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.GetBoolean();
    public static string[]? Targets(JsonElement args) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty("targets", out var v) ? v.EnumerateArray().Select(x => x.GetString()!).ToArray() : null;
    public static void Validate(string cmd, JsonElement args, string? target)
    {
        string fields = cmd switch
        {
            "workspace.new" => "name collapsed defaultDir default-dir", "workspace.flag" or "workspace.focus" or "workspace.filter" => "op",
            "sidebar.flagged-layout" => "layout", "session.close" => "targets undoable",
            "session.move" => "targets before after workspace",
            "session.new" => "name cwd workspace workspace-name create-workspace profile command command-mode no-select wait caller before after",
            _ => throw new ArgumentException("unsupported working-set operation")
        };
        if (cmd == "workspace.new" && (String(args, WorkspaceDefaultDirs.Key) ?? String(args, "default-dir")) is { } defaultDir &&
            WorkspaceDefaultDirs.ValidateSet(defaultDir) is { } defaultDirError) throw new ArgumentException(defaultDirError);
        var allowed = fields.Split(' ').ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (args.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null)) throw new ArgumentException("args must be an object");
        if (args.ValueKind == JsonValueKind.Object)
            foreach (var p in args.EnumerateObject())
            {
                if (!allowed.Contains(p.Name) || !seen.Add(p.Name)) throw new ArgumentException("unknown or duplicate option: " + p.Name);
                if (p.Name == "targets")
                {
                    if (p.Value.ValueKind != JsonValueKind.Array || p.Value.GetArrayLength() is < 1 or > 128 ||
                        p.Value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()) || v.GetString()!.Length > 4096))
                        throw new ArgumentException("targets must contain 1..128 nonempty selectors");
                }
                else if (p.Name is "collapsed" or "no-select" or "wait" or "create-workspace" or "undoable")
                { if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException(p.Name + " must be boolean"); }
                else if (p.Value.ValueKind != JsonValueKind.String || p.Value.GetString()!.Length > 16384)
                    throw new ArgumentException(p.Name + " must be bounded text");
            }
        foreach (var key in new[] { "before", "after", "workspace", "workspace-name" })
            if (seen.Contains(key) && string.IsNullOrWhiteSpace(String(args, key))) throw new ArgumentException(key + " cannot be empty");
        if (seen.Contains("before") && seen.Contains("after")) throw new ArgumentException("choose before or after, not both");
        if (Targets(args) is { } targets)
        {
            if (target is not null) throw new ArgumentException("batch targets cannot be combined with a single target");
            if (targets.Distinct(StringComparer.Ordinal).Count() != targets.Length) throw new ArgumentException("duplicate batch targets");
        }
        if (cmd == "session.move" && !seen.Overlaps(["before", "after", "workspace"])) throw new ArgumentException("batch move needs a workspace or anchor");
        if (cmd == "session.new" && (Bool(args, "create-workspace") || seen.Contains("workspace") && seen.Contains("workspace-name")))
            throw new ArgumentException("anchored new cannot create a workspace or specify two workspace sources");
        if (cmd is "workspace.new" or "workspace.filter" or "sidebar.flagged-layout" or "session.new" && target is not null)
            throw new ArgumentException(cmd + " does not accept a target");
        if (cmd is "workspace.flag" or "workspace.filter" or "workspace.focus")
        {
            string op = String(args, "op") ?? "toggle";
            if (op is not ("on" or "off" or "toggle") && !(cmd == "workspace.focus" && op == "clear")) throw new ArgumentException("invalid workspace operation");
            if (op == "clear" && target is not null) throw new ArgumentException("focus clear does not accept a workspace target");
        }
        if (cmd == "sidebar.flagged-layout" && (String(args, "layout") ?? "toggle") is not ("plain" or "flat" or "tree" or "toggle"))
            throw new ArgumentException("layout must be plain, flat, tree or toggle");
    }
}
