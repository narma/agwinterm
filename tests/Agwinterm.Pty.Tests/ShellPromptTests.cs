using System.Diagnostics;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class ShellPromptTests
{
    [Fact]
    public void HooksShellInstallationPreservesExistingProfilesAndIsIdempotent()
    {
        string home = Path.Combine(Path.GetTempPath(), "agwinterm-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            string rc = Path.Combine(home, ".bashrc");
            File.WriteAllText(rc, "# user's prompt\n");
            ShellIntegrationInstaller.InstallPosix(home);
            string once = File.ReadAllText(rc);
            ShellIntegrationInstaller.InstallPosix(home);
            Assert.Equal(once, File.ReadAllText(rc));
            Assert.StartsWith("# user's prompt\n", once);
            Assert.Contains("shell.sh", File.ReadAllText(Path.Combine(home, ".zshrc")));
            Assert.Contains("command-finished", File.ReadAllText(Path.Combine(home, ".local", "share", "agwinterm", "shell.sh")));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task OnlyTheRootShellReportsCompletionAfterItsFirstPrompt(bool installed, bool nested, bool legacy)
    {
        string path = Path.Combine(Path.GetTempPath(), "agwinterm-prompt-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            string prefix = legacy ? "$global:__agwSI = $true; $global:__agwPrompt = { 'USER_PROMPT' }; function global:prompt { & $global:__agwPrompt }\n" : "";
            string wrap = installed ? ShellIntegrationInstaller.Block : ShellIntegrationInstaller.PromptWrap;
            // Re-sourcing the current block is harmless; an old block must upgrade
            // without wrapping itself recursively or retaining the old behaviour.
            File.WriteAllText(path, prefix + wrap + "\n" + wrap
                + "\n[Console]::Write('FIRST'); $null = prompt; [Console]::Write('SECOND'); $null = prompt; [Console]::Write('END')");
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            };
            foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path }) start.ArgumentList.Add(arg);
            start.Environment["AGWINTERM"] = "1";
            start.Environment["AGWINTERM_PROMPT_OWNER"] = nested ? "windows:other" : "";
            using var process = Process.Start(start)!;
            var read = process.StandardOutput.ReadToEndAsync();
            Assert.True(process.WaitForExit(15000));
            Assert.Equal(0, process.ExitCode);
            string output = await read;
            const string marker = "\x1b]777;agwinterm;command-finished\x07";
            string first = output[output.IndexOf("FIRST", StringComparison.Ordinal)..output.IndexOf("SECOND", StringComparison.Ordinal)];
            Assert.DoesNotContain(marker, first);
            Assert.Equal(nested ? 0 : 1, output.Split(marker).Length - 1);
        }
        finally { File.Delete(path); }
    }
}
