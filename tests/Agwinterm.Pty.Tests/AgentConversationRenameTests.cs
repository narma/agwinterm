using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public sealed class AgentConversationRenameTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("agwinterm-agent-npm-").FullName;

    [Theory]
    [InlineData("codex", "@openai/codex/bin/codex.js", false)]
    [InlineData("codex", "@openai/codex/bin/codex.js", true)]
    [InlineData("claude", "@anthropic-ai/claude-code/cli.js", false)]
    [InlineData("claude", "@anthropic-ai/claude-code/bin/claude.exe", true)]
    public void WindowsNpmLaunchersKeepTitleArgumentsOutOfTheShell(string agent, string entry, bool local)
    {
        string modules = Path.Combine(_root, "node_modules");
        string shim = local ? Path.Combine(modules, ".bin") : _root;
        WriteFixture(Path.Combine(shim, agent + ".cmd"));
        string launch = WriteFixture(Path.Combine(modules, entry.Replace('/', Path.DirectorySeparatorChar)));
        string later = Path.Combine(_root, "later");
        WriteFixture(Path.Combine(later, agent + ".exe"));
        string node = WriteFixture(Path.Combine(later, "node.exe"));
        var profile = new ShellProfile { Env = new() { ["PATH"] = $"\"{shim}\"{Path.PathSeparator}{later}" } };
        string[] arguments = ["--resume", "id", "--name=Title \"quoted\" & %PATH% ! | < > ^"];
        bool native = launch.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

        var info = AgentConversationRename.StartInfo(agent, arguments, profile, _root);

        Assert.False(info.UseShellExecute);
        Assert.Equal(native ? launch : node, info.FileName);
        Assert.Equal(native ? arguments : [launch, .. arguments], info.ArgumentList.ToArray());
    }

    private static string WriteFixture(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
