using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>WSL boundaries: host paths and processes must not be confused with Linux ones.</summary>
public static class WslIntegration
{
    public static bool IsWsl(string? command) =>
        Path.GetFileName(command ?? "").Equals("wsl.exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(command, "wsl", StringComparison.OrdinalIgnoreCase);

    private static bool UserVariable(string key) => !key.StartsWith("AGWINTERM", StringComparison.OrdinalIgnoreCase)
        && !key.Equals("WSLENV", StringComparison.OrdinalIgnoreCase);

    private static int CommandIndex(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] is "--" or "--exec" or "-e" || !args[i].StartsWith('-')) return i;
            if (args[i] is "-d" or "--distribution" or "-u" or "--user" or "--cd" or "--shell-type") i++;
        }
        return args.Count;
    }

    // Keep only distribution/user selectors when running a helper, never the profile's command.
    public static string[] Selectors(string[]? args)
    {
        var result = new List<string>();
        args ??= [];
        int end = CommandIndex(args);
        for (int i = 0; i < end; i++)
        {
            if (args[i] is "--" or "--exec" or "-e") break;
            if (args[i] is "-d" or "--distribution" or "-u" or "--user")
            {
                if (++i == args.Length) throw new ArgumentException("WSL selector requires a value");
                result.Add(args[i - 1]); result.Add(args[i]);
            }
            else if (args[i].StartsWith("--distribution=", StringComparison.Ordinal) || args[i].StartsWith("--user=", StringComparison.Ordinal))
                result.Add(args[i]);
            else if (args[i] is "--cd" or "--shell-type") i++;
        }
        return result.ToArray();
    }

    public static void ExportEnvironment(Dictionary<string, string> env)
    {
        string existing = env.GetValueOrDefault("WSLENV") ?? Environment.GetEnvironmentVariable("WSLENV") ?? "";
        var entries = existing.Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (string key in env.Keys.Where(k => k != "WSLENV"))
        {
            // Identity must survive both Windows -> Linux and the CLI's interop return trip.
            if (key.StartsWith("AGWINTERM", StringComparison.OrdinalIgnoreCase))
                for (int i = 0; i < entries.Count; i++)
                    if (entries[i].Split('/')[0].Equals(key, StringComparison.OrdinalIgnoreCase)) entries[i] = key;
            if (!entries.Any(e => e.Split('/')[0].Equals(key, StringComparison.OrdinalIgnoreCase))) entries.Add(key);
        }
        env["WSLENV"] = string.Join(':', entries);
    }

    public static string[] LaunchArgs(string[]? args, string? cwd, IReadOnlyDictionary<string, string>? environment = null)
    {
        var result = (args ?? []).ToList();
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            // Place --cd before an explicit command. A restored cwd takes precedence over profile --cd.
            int end = CommandIndex(result);
            for (int i = end - 1; i >= 0; i--)
                if (result[i] == "--cd" && i + 1 < end) { result.RemoveRange(i, 2); end -= 2; }
                else if (result[i].StartsWith("--cd=", StringComparison.Ordinal)) { result.RemoveAt(i); end--; }
            result.InsertRange(0, ["--cd", cwd]);
        }
        if (environment is { Count: > 0 })
        {
            int exec = CommandIndex(result);
            if (exec == result.Count)
            {
                exec = result.Count;
                result.AddRange(["--exec", "/bin/sh", "-c", "exec \"${SHELL:-/bin/sh}\" -l"]);
            }
            else if (result[exec] is "--" or "-e" or "--exec") result[exec] = "--exec";
            else result.Insert(exec, "--exec");
            result.InsertRange(exec + 1, new[] { "env", "--" }.Concat(environment.Where(kv => UserVariable(kv.Key)).Select(kv => kv.Key + "=" + kv.Value)));
        }
        return result.ToArray();
    }

    public static string DecodeCwd(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        string path = raw;
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            path = path[7..];
            int slash = path.IndexOf('/');
            path = slash < 0 ? "" : path[slash..];
            path = Uri.UnescapeDataString(path);
        }
        if (path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':') path = path[1..];
        return path.Length >= 2 && path[1] == ':' ? path.Replace('/', '\\') : path;
    }

    public static string QuoteShell(string value) => "'" + value.Replace("'", "'\\''") + "'";

    public static string[] CommandArgs(ShellProfile profile, string command, string? cwd, bool interactive)
    {
        string script = interactive
            ? "\"${SHELL:-/bin/sh}\" -ic " + QuoteShell(command) + "; exec \"${SHELL:-/bin/sh}\" -l"
            : command;
        return LaunchArgs(Selectors(profile.Args).Concat(new[] { "--exec", "/bin/sh", "-lc", script }).ToArray(), cwd, profile.Env);
    }

    public static string WindowsPath(ShellProfile profile, string path) => Run(profile,
        "import subprocess,sys; print(subprocess.check_output(['wslpath','-w',sys.argv[1]],text=True).rstrip('\\n'))", path);

    public static string QuotedLinuxPaths(ShellProfile profile, IEnumerable<string> paths) => Run(profile,
        "import json,shlex,subprocess,sys; print(' '.join(shlex.quote(subprocess.check_output(['wslpath','-u',p],text=True).rstrip('\\n')) for p in json.loads(sys.argv[1])))",
        JsonSerializer.Serialize(paths));

    internal static string Resource(string name)
    {
        using var stream = typeof(WslIntegration).Assembly.GetManifestResourceStream("Agwinterm.Pty.Wsl." + name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string Run(ShellProfile profile, string script, params string[] args)
        => RunCore(profile, script, null, args);

    private static string RunCore(ShellProfile profile, string script, string? input, string[]? args = null, int timeoutMs = 30000, string[]? nativeCommand = null)
    {
        var psi = new ProcessStartInfo(profile.Command) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        if (profile.Env is { Count: > 0 })
        {
            var environment = profile.Env.Where(kv => !kv.Key.StartsWith("AGWINTERM", StringComparison.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key, kv => kv.Value);
            ExportEnvironment(environment);
            foreach (var pair in environment) psi.Environment[pair.Key] = pair.Value;
        }
        var helper = Selectors(profile.Args).Concat(new[] { "--exec" }).Concat(nativeCommand ?? ["python3", "-c", script]).Concat(args ?? []).ToArray();
        foreach (string arg in LaunchArgs(helper, null, profile.Env)) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new IOException("Could not start WSL");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var write = Task.Run(async () =>
        {
            if (input is not null) await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        });
        if (!process.WaitForExit(timeoutMs)) { process.Kill(entireProcessTree: true); throw new TimeoutException("WSL helper timed out"); }
        string output = stdout.GetAwaiter().GetResult();
        string error = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new IOException((nativeCommand is null ? "WSL (python3 and Windows interop required): " : "WSL native helper: ") + error.Trim());
        write.GetAwaiter().GetResult();
        return output.TrimEnd('\r', '\n');
    }

    public static string Install(ShellProfile? profile, string kind, bool remove = false)
    {
        if (profile is null || !IsWsl(profile.Command)) return kind switch
        {
            "hooks" => AgentHooks.Install(), "skill" => AgentSkill.Install(),
            "shell" => ShellIntegrationInstaller.Install(),
            "cli" => remove ? CliInstaller.Uninstall() : CliInstaller.Install(),
            _ => throw new ArgumentException("Unknown integration")
        };
        return RunCore(profile, Resource("install.py"), JsonSerializer.Serialize(new[] { kind, remove ? "remove" : "install",
            Path.Combine(CliInstaller.CliDir(), "agwintermctl.exe"), AgentSkill.SkillMarkdown,
            Resource("hook.py"), Resource("shell.sh"),
            Path.Combine(CliInstaller.CliDir(), "agwinterm-hook.exe"),
            Path.Combine(CliInstaller.CliDir(), "agwinterm-hook-linux-x64") }));
    }

    public static IReadOnlyDictionary<int, AgentResume.ProcRow> Ancestry(ShellProfile profile, int pid, string pane)
    {
        string json = Run(profile, Resource("ancestry.py"), pid.ToString(), pane);
        return JsonSerializer.Deserialize<List<AgentResume.ProcRow>>(json)!.ToDictionary(p => p.Pid);
    }

    public static IReadOnlyDictionary<int, AgentResume.ProcRow> NativeAncestry(ShellProfile profile, int pid, string pane)
    {
        // UNC access uses the distribution's default user. A profile with -u may need its own
        // credentials to read procfs; execute the installed native helper under those selectors.
        string json = RunCore(profile, "", null, [pid.ToString(), pane], timeoutMs: 5000,
            nativeCommand: ["/bin/sh", "-c", "exec \"$HOME/.local/share/agwinterm/agwinterm-hook\" --ancestry \"$@\"", "agwinterm-ancestry"]);
        return JsonSerializer.Deserialize<List<AgentResume.ProcRow>>(json)!.ToDictionary(p => p.Pid);
    }

    public static Dictionary<string, string> Foreground(ShellProfile profile, IEnumerable<string> panes, IEnumerable<string> denied, int timeoutMs)
        => JsonSerializer.Deserialize<Dictionary<string, string>>(RunCore(profile, Resource("foreground.py"), null,
            [JsonSerializer.Serialize(panes), JsonSerializer.Serialize(denied)], timeoutMs))!;
}
