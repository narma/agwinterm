namespace Agwinterm.Core;

/// <summary>Portable baseline for newly created terminals; extended features are queried at runtime.</summary>
public static class TerminalIdentity
{
    // Available in standard ncurses installations; do not require a Ghostty entry on remote hosts.
    public const string Term = "xterm-256color";
}
