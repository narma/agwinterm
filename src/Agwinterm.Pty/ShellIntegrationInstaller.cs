using System.Diagnostics;
using System.IO;

namespace Agwinterm.Pty;

/// <summary>
/// Installs prompt-safe shell integration into the user's PowerShell profile (agterm-style
/// self-setup). Appends an idempotent, sentinel-delimited block to the END of the CurrentUser
/// powershell.exe profile so it runs AFTER the profile's prompt setup (e.g. oh-my-posh): it
/// WRAPS the existing prompt (rather than replacing it) to also emit OSC 7 with the live working
/// directory, and only activates in agwinterm sessions (guarded by $env:AGWINTERM). The emulator's
/// OscDispatch(7) then keeps Cwd current, so persistence saves/restores the real directory after `cd`.
/// </summary>
public static class ShellIntegrationInstaller
{
    private const string Begin = "# >>> agwinterm shell integration >>>";
    private const string End = "# <<< agwinterm shell integration <<<";

    /// <summary>The guarded block appended to $PROFILE. Wraps the existing prompt; emits OSC 7 (BEL-terminated).</summary>
    public static readonly string Block =
        Begin + "\n" +
        """
        if ($env:AGWINTERM -eq '1' -and $global:__agwPromptVersion -ne 2) {
          if (-not $global:__agwSI) { $global:__agwPrompt = $function:prompt }
          $global:__agwSI = $true
          $global:__agwPromptVersion = 2
          $global:__agwFirst = $true
          $global:__agwPromptSeq = 0
          if (-not $env:AGWINTERM_PROMPT_OWNER) { $env:AGWINTERM_PROMPT_OWNER = "windows:$PID" }
          function global:prompt {
            if ($env:AGWINTERM_PROMPT_TOKEN -and $env:AGWINTERM_PROMPT_OWNER -eq "windows:$PID") {
              $global:__agwPromptSeq++
              [Console]::Write("$([char]27)]777;agwinterm;command-finished;$env:AGWINTERM_PROMPT_TOKEN;$global:__agwPromptSeq$([char]7)")
            }
            $d = (Get-Location).ProviderPath
            [Console]::Write("$([char]27)]7;file://$env:COMPUTERNAME/$(($d -replace '\\','/'))$([char]7)")
            if ($global:__agwPrompt) { & $global:__agwPrompt } else { "PS $d> " }
          }
        }
        """ + "\n" + End;

    /// <summary>
    /// The prompt-wrap script run at shell LAUNCH (out-of-the-box live cwd, no $PROFILE edit). Same OSC 7
    /// emit as <see cref="Block"/>, guarded by the SAME <c>$global:__agwSI</c> sentinel so the launch wrap
    /// and an installed $PROFILE block never double-wrap. Passed via <c>-EncodedCommand</c> so it runs after
    /// the user's profile (oh-my-posh) and WRAPS the existing prompt instead of replacing it.
    /// </summary>
    public static readonly string PromptWrap =
        """
        if ($global:__agwPromptVersion -ne 2) {
          # Upgrade an old installed wrapper without capturing it as its own
          # original prompt (that wrapper already uses __agwPrompt).
          if (-not $global:__agwSI) { $global:__agwPrompt = $function:prompt }
          $global:__agwSI = $true
          $global:__agwPromptVersion = 2
          $global:__agwFirst = $true
          $global:__agwPromptSeq = 0
          if (-not $env:AGWINTERM_PROMPT_OWNER) { $env:AGWINTERM_PROMPT_OWNER = "windows:$PID" }
          function global:prompt {
            $ok = $?
            $e = [char]27; $b = [char]7
            $ec = $global:LASTEXITCODE; if ($null -eq $ec) { $ec = if ($ok) { 0 } else { 1 } }
            if ($global:__agwFirst) { $global:__agwFirst = $false } else {
              [Console]::Write("$e]133;D;$ec$b")
            }
            if ($env:AGWINTERM_PROMPT_TOKEN -and $env:AGWINTERM_PROMPT_OWNER -eq "windows:$PID") {
              $global:__agwPromptSeq++
              [Console]::Write("$e]777;agwinterm;command-finished;$env:AGWINTERM_PROMPT_TOKEN;$global:__agwPromptSeq$b")
            }
            [Console]::Write("$e]133;A$b")
            $d = (Get-Location).ProviderPath
            [Console]::Write("$e]7;file://$env:COMPUTERNAME/$(($d -replace '\\','/'))$b")
            $p = if ($global:__agwPrompt) { & $global:__agwPrompt } else { "PS $d> " }
            "$p$e]133;B$b"
          }
        }
        """;

    /// <summary>Base64 (UTF-16LE) of <see cref="PromptWrap"/> for <c>powershell.exe -EncodedCommand</c>.</summary>
    public static string PromptWrapEncoded =>
        System.Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(PromptWrap));

    public static string? RefreshIfInstalled() => ProfileBlocks.RefreshIfInstalled(Begin, End, Block, "shell integration");

    /// <summary>Git Bash/MSYS2 shells use the same prompt signal as WSL. Called by the
    /// hooks installer so resume binding and its exit cleanup are installed together.</summary>
    internal static string InstallPosix(string home)
    {
        string path = Path.Combine(home, ".local", "share", "agwinterm", "shell.sh");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, WslIntegration.Resource("shell.sh"));
        const string begin = "# >>> agwinterm shell >>>", end = "# <<< agwinterm shell <<<";
        const string block = begin + "\n[ -r \"$HOME/.local/share/agwinterm/shell.sh\" ] && . \"$HOME/.local/share/agwinterm/shell.sh\"\n" + end;
        foreach (string rc in new[] { ".bashrc", ".zshrc" })
        {
            string profile = Path.Combine(home, rc);
            string existing = File.Exists(profile) ? File.ReadAllText(profile) : "";
            var state = ProfileBlocks.Inspect(existing, begin, end, block, out string updated);
            if (state == ProfileBlockState.Stale) File.WriteAllText(profile, updated);
            else if (state == ProfileBlockState.Missing) File.WriteAllText(profile, existing + "\n" + block + "\n");
        }
        return "Bash/zsh shell integration -> " + path + " (restart shells)";
    }

    /// <summary>
    /// Resolve the CurrentUser/CurrentHost powershell.exe profile path via powershell itself
    /// (so a OneDrive-redirected Documents folder is honored). Falls back to the conventional path.
    /// </summary>
    public static string ProfilePath()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"$PROFILE.CurrentUserCurrentHost\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                string path = outp.Trim();
                if (!string.IsNullOrWhiteSpace(path)) return path;
            }
        }
        catch { /* fall through to the conventional location */ }

        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(docs, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1");
    }

    /// <summary>Append the block to the profile if not already present. Idempotent. Returns a summary.</summary>
    public static string Install()
    {
        string path = ProfilePath();
        try
        {
            string existing = File.Exists(path) ? File.ReadAllText(path) : "";
            var state = ProfileBlocks.Inspect(existing, Begin, End, Block, out string updated);
            if (state == ProfileBlockState.Stale) {
                File.WriteAllText(path, updated);
                return "shell integration updated -> " + path;
            }
            if (state != ProfileBlockState.Missing)
                return "shell integration already installed -> " + path;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string sep = existing.Length == 0 ? "" : (existing.EndsWith("\n") ? "\n" : "\n\n");
            File.AppendAllText(path, sep + Block + "\n");
            return "installed shell integration -> " + path + " (restart sessions to track the working directory)";
        }
        catch (Exception ex)
        {
            return "failed to install shell integration: " + ex.Message;
        }
    }
}
