using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Agwinterm.Pty;

public static class AgentConversationRename
{
    public static bool Supports(AgentConversation? conversation) =>
        conversation is { Agent: "codex" or "claude" } && Guid.TryParseExact(conversation.Id, "D", out _);

    /// <summary>Save the title through the agent's own process, independently of the terminal input stream.</summary>
    public static async Task RenameAsync(AgentConversation conversation, string name, ShellProfile? profile, string? cwd)
    {
        name = name.Trim();
        if (!Supports(conversation) || name.Length == 0 || name.Any(char.IsControl))
            throw new ArgumentException("A valid conversation ID and title are required");

        bool codex = conversation.Agent == "codex";
        string[] args = codex ? ["app-server"] :
            ["--resume", conversation.Id, "--name=" + name, "--print", "--input-format", "stream-json",
                "--output-format", "stream-json", "--verbose", "--safe-mode"];
        using var process = Process.Start(StartInfo(conversation.Agent, args, profile, cwd))
            ?? throw new IOException("Could not start the agent CLI");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        var stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            if (codex)
                await RenameCodexAsync(process, conversation.Id, name, token).ConfigureAwait(false);
            else
            {
                process.StandardInput.Close();
                bool confirmed = false;
                while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                {
                    using var message = JsonDocument.Parse(line);
                    var root = message.RootElement;
                    confirmed |= StringProperty(root, "type") == "system"
                        && StringProperty(root, "subtype") == "session_title_changed"
                        && StringProperty(root, "title") == name
                        && string.Equals(StringProperty(root, "session_id"), conversation.Id, StringComparison.OrdinalIgnoreCase);
                }
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                if (!confirmed || process.ExitCode != 0)
                    throw new IOException("Claude Code did not confirm the new title");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new TimeoutException("The agent CLI timed out");
        }
        finally
        {
            process.StandardInput.Close();
            try
            {
                if (!process.WaitForExit(300)) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { } // The process exited between the wait and kill.
            try { await stderr.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    internal static ProcessStartInfo StartInfo(string agent, string[] args, ShellProfile? profile, string? cwd)
    {
        bool wsl = WslIntegration.IsWsl(profile?.Command);
        var info = new ProcessStartInfo
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (profile?.Env is { } environment)
            foreach (var pair in environment)
                if (!wsl || pair.Key.Equals("WSLENV", StringComparison.OrdinalIgnoreCase))
                    info.Environment[pair.Key] = pair.Value;

        if (wsl)
        {
            info.FileName = profile!.Command;
            // Keep the actual arguments separate from the shell program. Prefer the profile's PATH
            // and the standard user bin directory before consulting the login shell.
            const string lookup = """
                if command -v "$1" >/dev/null 2>&1; then exec "$@"; fi
                if [ -x "$HOME/.local/bin/$1" ]; then agent="$HOME/.local/bin/$1"; shift; exec "$agent" "$@"; fi
                exec "${SHELL:-/bin/sh}" -lc 'exec "$@"' agwinterm-agent "$@"
                """;
            string[] command = [.. WslIntegration.Selectors(profile.Args), "--exec", "/bin/sh", "-c",
                lookup, "agwinterm-agent", agent, .. args];
            args = WslIntegration.LaunchArgs(command, cwd, profile.Env);
        }
        else
        {
            info.Environment.TryGetValue("PATH", out var path);
            var (executable, script) = ResolveWindowsAgent(agent, path);
            info.FileName = executable;
            if (script is not null) info.ArgumentList.Add(script);
            if (cwd is not null && Directory.Exists(cwd)) info.WorkingDirectory = cwd;
        }
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    private static (string Executable, string? Script) ResolveWindowsAgent(string agent, string? path)
    {
        string[] directories = (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.TrimEndingDirectorySeparator(dir.Trim().Trim('"')))
            .Where(Path.IsPathFullyQualified).ToArray();
        foreach (string directory in directories)
        {
            string exe = Path.Combine(directory, agent + ".exe");
            if (File.Exists(exe)) return (exe, null);
            if (!File.Exists(Path.Combine(directory, agent + ".cmd"))) continue;

            // npm puts global shims next to node_modules and local shims in node_modules/.bin.
            // Launch the official package entry directly so titles never reach a batch interpreter.
            string modules = Path.GetFileName(directory).Equals(".bin", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(directory)! : Path.Combine(directory, "node_modules");
            string package = agent == "codex" ? Path.Combine(modules, "@openai", "codex")
                : Path.Combine(modules, "@anthropic-ai", "claude-code");
            if (agent == "claude")
            {
                string native = Path.Combine(package, "bin", "claude.exe");
                if (File.Exists(native)) return (native, null);
            }

            string script = agent == "codex" ? Path.Combine(package, "bin", "codex.js") : Path.Combine(package, "cli.js");
            if (!File.Exists(script)) continue;
            string node = Path.Combine(directory, "node.exe");
            if (File.Exists(node)) return (node, script);
            string? onPath = directories.Select(dir => Path.Combine(dir, "node.exe")).FirstOrDefault(File.Exists);
            if (onPath is not null) return (onPath, script);
        }
        return (agent + ".exe", null);
    }

    private static async Task RenameCodexAsync(Process process, string id, string name, CancellationToken token)
    {
        async Task Send(object request) => await process.StandardInput
            .WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), token).ConfigureAwait(false);

        async Task<JsonElement> Receive(int requestId)
        {
            while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                using var message = JsonDocument.Parse(line);
                var root = message.RootElement;
                if (!root.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number
                    || !responseId.TryGetInt32(out int value) || value != requestId) continue;
                if (root.TryGetProperty("result", out var result)) return result.Clone();
                break;
            }
            throw new IOException("Codex App Server rejected the request or stopped responding");
        }

        await Send(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "agwinterm", version = "1" } } });
        await Receive(1);
        await Send(new { method = "initialized" });
        await Send(new { id = 2, method = "thread/name/set", @params = new { threadId = id, name } });
        await Receive(2);
        await Send(new { id = 3, method = "thread/read", @params = new { threadId = id, includeTurns = false } });
        var read = await Receive(3);
        if (!read.TryGetProperty("thread", out var thread) || StringProperty(thread, "name") != name)
            throw new IOException("Codex App Server did not confirm the new title");
    }

    private static string? StringProperty(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
