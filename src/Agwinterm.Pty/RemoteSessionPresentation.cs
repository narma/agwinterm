using Agwinterm.Core;

namespace Agwinterm.Pty;

/// <summary>UI-side adapter over bounded presentation and semantic input channels.
/// Captures retain the latest complete immutable frame; they never block for IPC.</summary>
internal sealed class RemoteSessionPresentation(ServerSession session) : ISessionPresentation
{
    public bool IsRemote => true;
    public TerminalFrame.Lease? Capture(int scrollOffset,int cellWidth,int cellHeight)
        => session.CapturePresentation(scrollOffset,cellWidth,cellHeight);
    public TerminalInputAcceptance Submit(TerminalInput input) => session.SubmitInput(input);
    public TerminalInputAcceptance Execute(TerminalCommand command)
        => session.ExecutePresentation(command);
    public ValueTask<TerminalQueryResult> QueryAsync(TerminalQuery query,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new TerminalQueryResult(query.Incarnation,query.AttachmentEpoch,query.Generation,"unsupported",[]));
    }
}
