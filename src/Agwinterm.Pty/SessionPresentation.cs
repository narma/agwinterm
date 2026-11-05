using Agwinterm.Core;

namespace Agwinterm.Pty;

public abstract record TerminalInput
{
    public sealed record Key(uint VirtualKey, uint Action, uint Modifiers, string TextValue,
        uint Unshifted, uint ConsumedModifiers, int WindowsUnicode, uint WindowsControlState, uint ScanCode, string WindowsText = "") : TerminalInput;
    public sealed record Text(string Value) : TerminalInput;
    public sealed record Paste(string Value) : TerminalInput;
    public sealed record Mouse(uint Action, uint Button, uint Modifiers, float X, float Y, uint CellWidth, uint CellHeight,
        int Column=-1,int Row=-1) : TerminalInput;
    public sealed record Focus(bool Focused) : TerminalInput;
    public sealed record Raw(ReadOnlyMemory<byte> Bytes) : TerminalInput;
    public sealed record FramePresented(ulong Sequence) : TerminalInput;
    public sealed record Viewport(ulong Revision, int Columns, int Rows, int CellWidth, int CellHeight,
        int ScrollOffset, bool Visible) : TerminalInput;
}

public enum TerminalInputAcceptance { Accepted, Closed, Overflow, DeliveryUncertain }
public abstract record TerminalCommand
{
    public sealed record Resize(int Columns, int Rows, int CellWidth, int CellHeight, ulong ViewportRevision) : TerminalCommand;
    public sealed record Viewport(int ScrollOffset, bool Visible, ulong ViewportRevision) : TerminalCommand;
    public sealed record InjectOutput(ReadOnlyMemory<byte> Bytes) : TerminalCommand;
    public sealed record Clear(bool History) : TerminalCommand;
    public sealed record Reset : TerminalCommand;
    public sealed record ScrollbackLimit(int Rows) : TerminalCommand;
    public sealed record Theme(string Colors, string Palette, string Cursor) : TerminalCommand;
}
public sealed record TerminalQuery(string Incarnation, ulong AttachmentEpoch, long Generation,
    string Operation, int StartRow = 0, int RowCount = 0, string Text = "", int ResultLimit = 100000);
public sealed record TerminalQueryResult(string Incarnation, ulong AttachmentEpoch, long Generation,
    string Status, IReadOnlyList<string> Rows, bool Truncated = false);

/// <summary>
/// UI-facing semantics, independent of direct/local calls or a hosted transport. Capture never
/// performs per-cell RPC. A returned lease is immutable until disposed; do not dispose a lease
/// concurrently with its reader. Inputs preserve accepted ordering, and queries are cancellable
/// and fenced to their incarnation/attachment/generation. Raw writes are never re-encoded.
/// </summary>
public interface ISessionPresentation
{
    bool IsRemote { get; }
    TerminalFrame.Lease? Capture(int scrollOffset, int cellWidth, int cellHeight);
    TerminalInputAcceptance Submit(TerminalInput input);
    TerminalInputAcceptance Execute(TerminalCommand command);
    ValueTask<TerminalQueryResult> QueryAsync(TerminalQuery query, CancellationToken cancellationToken);
}
