using Agwinterm.Core;
using System.Text.Json.Serialization;

namespace Agwinterm.Pty;

public sealed record AgentConversation(string Agent, string Id)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Directory { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Title { get; init; }
}

/// <summary>Recovery belongs to a workload, not its replaceable UI attachment. Sharing this
/// state lets an accepted binding finish after a transport replacement without writing into
/// a discarded pane. Binding writes run on the UI thread; request ordering uses Interlocked.</summary>
public sealed class AgentRecoveryState
{
    public string? Command;
    public AgentConversation? Conversation;
    public ShellCompletion Completion = new("", "");
    public string? Token;
    public long Generation;
    public long CompletionBaseline;
    public int Sequence;
    private int _restartReservation;

    /// <summary>A saved command is presence evidence only when the exact hosted shell
    /// survived and has not reached another prompt since the binding was accepted.</summary>
    public bool HasSurvivingBinding(bool adopted) => adopted && !string.IsNullOrWhiteSpace(Command)
        && ShellCompletion.ValidToken(Token) && Token == Completion.Token
        && Generation >= 0 && Completion.Checkpoint <= Generation;

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
        string? replacement = Completion.Checkpoint > 0 ? null : command;
        if (replacement is null || replacement != Command) Conversation = null;
        Command = replacement;
        Token = Completion.Token;
        Generation = 0;
        // A pending request owns its baseline. The fallback must not change it.
        if (Volatile.Read(ref Sequence) == reservation) Volatile.Write(ref CompletionBaseline, 0);
        return true;
    }
}
