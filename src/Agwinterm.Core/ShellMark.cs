namespace Agwinterm.Core;

/// <summary>One prompt→command→output record from FTCS (OSC 133). Lines are buffer-absolute
/// (history + live row); they shift as history trims and drop off when trimmed away.</summary>
public sealed class ShellMark
{
    public int PromptLine;        // 133;A — the prompt started on this line
    public int CommandLine = -1;  // 133;B — command input line (where the user types)
    public int OutputLine = -1;   // 133;C — command output starts here (optional)
    public int EndLine = -1;      // 133;D — command finished (the next prompt's line)
    public int? ExitCode;         // from 133;D;<code>
}
