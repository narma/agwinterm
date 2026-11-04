using Agwinterm.Core;

namespace Agwinterm.Pty;

/// <summary>Recovery belongs to a workload, not its replaceable UI attachment. Sharing this
/// state lets an accepted binding finish after a transport replacement without writing into
/// a discarded pane. Binding writes run on the UI thread; request ordering uses Interlocked.</summary>
public sealed class AgentRecoveryState
{
    public string? Command;
    public ShellCompletion Completion = new("", "");
    public string? Token;
    public long Generation;
    public long CompletionBaseline;
    public int Sequence;

    /// <summary>Reserve before the replacement can start. An old pending lookup cannot land
    /// in the new workload; a request from the replacement supersedes this reservation.</summary>
    public int BeginRestart(ShellCompletion completion)
    {
        Completion = completion;
        return Interlocked.Increment(ref Sequence);
    }

    /// <summary>Only the unchanged reservation may install fallback recovery. A completed or
    /// still-pending newer bind (including an explicit clear) owns the state instead.</summary>
    public bool FinishRestart(int reservation, string? command)
    {
        if (Volatile.Read(ref Sequence) != reservation) return false;
        Command = command;
        Token = Completion.Token;
        Generation = 0;
        Volatile.Write(ref CompletionBaseline, 0);
        return true;
    }
}
