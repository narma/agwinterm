using System.IO;

namespace Agwinterm.Pty;

/// <summary>
/// Installs a generic, agent-agnostic status bridge into the user's PowerShell profile, for agents
/// that lack native hooks (unlike Claude Code, which uses <see cref="AgentHooks"/>). Appends a guarded,
/// sentinel-delimited block to the END of the CurrentUser powershell.exe profile that — only inside an
/// agwinterm session ($env:AGWINTERM) — watches for commands whose name matches $env:AGWINTERM_AGENT_RE
/// (a regex; a sensible default is seeded). When such a command is submitted it emits OSC 7501 "working";
/// when it finishes (detected at the next prompt) it clears the producer. Idempotent.
/// </summary>
public static class GenericAgentInstaller
{
    private const string Begin = "# >>> agwinterm generic agent status >>>";
    private const string End = "# <<< agwinterm generic agent status <<<";

    /// <summary>The guarded block appended to $PROFILE. Wraps the prompt + a PSReadLine Enter handler.</summary>
    public static readonly string Block =
        Begin + "\n" +
        """
        if ($env:AGWINTERM -eq '1' -and -not $global:__agwAgent) {
          $global:__agwAgent = $true
          if (-not $env:AGWINTERM_AGENT_RE) { $env:AGWINTERM_AGENT_RE = 'aider|gemini|cursor|copilot|goose|opencode|amp|pi' }
          function global:__agwStatus([string]$s) {
            [Console]::Write("$([char]27)]7501;state=$s$([char]7)")
          }
          function global:__agwGenericAgent([string]$line) {
            # These agents have dedicated hooks. Exclude them even from a legacy/custom regex:
            # a generic OSC root would mask their permission and turn-completion reports.
            return $line -and $line -notmatch '^\s*(claude|codex|devin)\b' -and $line -match ('^\s*(' + $env:AGWINTERM_AGENT_RE + ')\b')
          }
          if (Get-Module -ListAvailable PSReadLine) {
            Set-PSReadLineKeyHandler -Key Enter -ScriptBlock {
              $line = $null; $cur = $null
              [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref]$line, [ref]$cur)
              if (__agwGenericAgent $line) { __agwStatus 'working' }
              [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()
            }
            $global:__agwAgentPrompt = $function:prompt
            function global:prompt {
              $h = Get-History -Count 1
              if ($h -and (__agwGenericAgent $h.CommandLine) -and $h.Id -ne $global:__agwLastH) {
                $global:__agwLastH = $h.Id; __agwStatus 'clear'
              }
              if ($global:__agwAgentPrompt) { & $global:__agwAgentPrompt } else { "PS $((Get-Location).Path)> " }
            }
          }
        }
        """ + "\n" + End;

    /// <summary>Append the block to the profile if not already present; replace an installed block whose
    /// content is stale (older bridge version — e.g. before pi joined the default agent regex). Idempotent.</summary>
    public static string Install()
    {
        string path = ShellIntegrationInstaller.ProfilePath();
        try
        {
            string existing = File.Exists(path) ? File.ReadAllText(path) : "";
            switch (ProfileBlocks.Inspect(existing, Begin, End, Block, out string updated))
            {
                case ProfileBlockState.Current: return "generic agent status already installed -> " + path;
                case ProfileBlockState.Corrupted: return "generic agent block is corrupted (no end sentinel) — fix " + path + " by hand";
                case ProfileBlockState.Stale:
                    File.WriteAllText(path, updated);
                    return "updated generic agent status to the current version -> " + path;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string sep = existing.Length == 0 ? "" : (existing.EndsWith("\n") ? "\n" : "\n\n");
            File.AppendAllText(path, sep + Block + "\n");
            return "installed generic agent status (regex $env:AGWINTERM_AGENT_RE) -> " + path;
        }
        catch (Exception ex)
        {
            return "failed to install generic agent status: " + ex.Message;
        }
    }

    /// <summary>Refresh an already-installed block at startup (never installs fresh).</summary>
    public static string? RefreshIfInstalled() => ProfileBlocks.RefreshIfInstalled(Begin, End, Block, "generic agent bridge");
}
