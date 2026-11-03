namespace Agwinterm.Pty;

/// <summary>Viewer-side outcome knowledge, not an origin answer registry or a retry queue.</summary>
public sealed class AgtermQuestionDelivery
{
    private long _attempt;
    public string? Id { get; private set; }
    public string? Outcome { get; private set; }
    public long Begin(string id)
    { Id = id; Outcome = "awaiting-origin"; return ++_attempt; }
    public void Observe(long attempt, string outcome)
    {
        if (attempt != _attempt) return;
        if (outcome is not ("answered" or "escaped" or "cancelled" or "uncertain")) throw new ArgumentException("Not an origin outcome");
        // A late failed read cannot undo authoritative knowledge.
        if (Outcome is "answered" or "escaped" or "cancelled") return;
        Outcome = outcome;
    }
    public void Disconnected()
    { if (Outcome == "awaiting-origin") Outcome = "uncertain"; }
}
