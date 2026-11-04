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
    private int _restartReservation;

    /// <summary>Reserve before the replacement can start. An old pending lookup cannot land
    /// in the new workload; a request from the replacement supersedes this reservation.</summary>
    public int BeginRestart(ShellCompletion completion)
    {
        Completion = completion;
        return _restartReservation = Interlocked.Increment(ref Sequence);
    }

    /// <summary>A saved replacement binding (including an explicit clear) wins. An accepted
    /// request alone may still fail validation: install the fallback without advancing Sequence,
    /// so that pending request can replace it later if it succeeds.</summary>
    public bool FinishRestart(int reservation, string? command)
    {
        if (_restartReservation != reservation || Token == Completion.Token) return false;
        Command = Completion.Checkpoint > 0 ? null : command;
        Token = Completion.Token;
        Generation = 0;
        // A pending request owns its baseline. The fallback must not change it.
        if (Volatile.Read(ref Sequence) == reservation) Volatile.Write(ref CompletionBaseline, 0);
        return true;
    }
}
