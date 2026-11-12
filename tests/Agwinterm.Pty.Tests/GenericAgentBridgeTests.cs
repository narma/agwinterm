using System.Diagnostics;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class GenericAgentBridgeTests
{
    [Theory]
    [InlineData("claude --resume plan", false)]
    [InlineData(" CODEX", false)]
    [InlineData("devin", false)]
    [InlineData("aider", true)]
    [InlineData("myagent --work", true)]
    [InlineData("echo claude", false)]
    public async Task InstalledBridgeDoesNotMaskHookAgentsEvenWithLegacyRegex(string line, bool generic)
    {
        string path = Path.Combine(Path.GetTempPath(), "agwinterm-generic-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            // Stub module discovery only: execute the real installed predicate and emitter
            // without changing PSReadLine handlers or the user's profile in this test process.
            File.WriteAllText(path, "function Get-Module {}\n" + GenericAgentInstaller.Block +
                "\nif (__agwGenericAgent $env:TEST_LINE) { __agwStatus 'working'; __agwStatus 'clear' }");
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path }) start.ArgumentList.Add(arg);
            start.Environment["AGWINTERM"] = "1";
            start.Environment["AGWINTERM_AGENT_RE"] = "claude|codex|devin|aider|myagent";
            start.Environment["TEST_LINE"] = line;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(15000));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await error);
            Assert.Equal(generic ? "\x1b]7501;state=working\a\x1b]7501;state=clear\a" : "", await output);
        }
        finally { File.Delete(path); }
    }
}
