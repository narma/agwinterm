namespace Agwinterm.Core;

/// <summary>
/// Push-based agent state for a session (agterm model): set explicitly by the agent
/// via the control API or OSC 7501, never inferred from human-readable terminal text.
/// Surfaced as a glyph/indicator and in the agents sidebar.
/// </summary>
public enum AgentStatus
{
    Idle,       // no indicator
    Active,     // agent working
    Blocked,    // awaiting the user
    Completed,  // finished
}
