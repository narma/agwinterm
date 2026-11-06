using System.Buffers.Binary;
using Agwinterm.Pty.Proto;
using Google.Protobuf;

namespace Agwinterm.Pty;

internal static class PresentationInputCodec
{
    internal const int MaximumBytes = OwnerInputQueue.MaximumBytes;
    public static byte[] Encode(TerminalInput input, string incarnation, ulong epoch, ulong sequence)
    {
        if(input is TerminalInput.Key keyInput && (System.Text.Encoding.UTF8.GetByteCount(keyInput.TextValue) > 4096 || keyInput.WindowsText.Length > 128))
            throw new InvalidDataException("Key text exceeds the native encoder limit.");
        if(input is TerminalInput.Mouse mouse && (!float.IsFinite(mouse.X) || !float.IsFinite(mouse.Y) || mouse.Action > 2 ||
            mouse.CellWidth is < 1 or > 65535 || mouse.CellHeight is < 1 or > 65535 ||
            mouse.Column is < -1 or >= 10000 || mouse.Row is < -1 or >= 10000 || (mouse.Column < 0) != (mouse.Row < 0)))
            throw new InvalidDataException("Invalid mouse coordinates.");
        var p = new PresentationInput { Incarnation = incarnation, AttachmentEpoch = epoch, Sequence = sequence };
        switch (input)
        {
            case TerminalInput.Key k:
                p.Kind=1; p.VirtualKey=k.VirtualKey; p.Action=k.Action; p.Modifiers=k.Modifiers; p.Text=k.TextValue;
                p.Unshifted=k.Unshifted; p.ConsumedModifiers=k.ConsumedModifiers; p.WindowsUnicode=k.WindowsUnicode;
                p.WindowsControlState=k.WindowsControlState; p.ScanCode=k.ScanCode; break;
            case TerminalInput.Text t: p.Kind=2; p.Text=t.Value; break;
            case TerminalInput.Paste t: p.Kind=3; p.Text=t.Value; break;
            case TerminalInput.Mouse m:
                p.Kind=4; p.Action=m.Action; p.Button=m.Button; p.Modifiers=m.Modifiers; p.X=m.X; p.Y=m.Y;
                p.CellWidth=m.CellWidth; p.CellHeight=m.CellHeight;
                if(m.Column >= 0 && m.Row >= 0) { p.HasCellPosition=true; p.CellColumn=(uint)m.Column; p.CellRow=(uint)m.Row; }
                break;
            case TerminalInput.Focus f: p.Kind=5; p.Focused=f.Focused; break;
            case TerminalInput.Raw r: p.Kind=6; p.Raw=ByteString.CopyFrom(r.Bytes.Span); break;
            case TerminalInput.Command command:
                p.Kind=9; p.OwnerCommand=command.Value switch {
                    TerminalCommand.InjectOutput inject=>new() { Kind=1,Bytes=ByteString.CopyFrom(inject.Bytes.Span) },
                    TerminalCommand.Clear clear=>new() { Kind=2,History=clear.History },
                    TerminalCommand.Reset=>new() { Kind=3 },
                    TerminalCommand.ScrollbackLimit limit when limit.Rows >= 0=>new() { Kind=4,Rows=limit.Rows },
                    _=>throw new ArgumentException("Unsupported ordered owner command.") };
                break;
            case TerminalInput.FramePresented f: p.Kind=7; p.FrameSequence=f.Sequence; break;
            case TerminalInput.Viewport v:
                ValidateViewport(v);
                p.Kind=8; p.Viewport=new PresentationViewport { Revision=v.Revision,Cols=(uint)v.Columns,Rows=(uint)v.Rows,
                    CellWidth=(uint)v.CellWidth,CellHeight=(uint)v.CellHeight,ScrollOffset=(uint)v.ScrollOffset,Visible=v.Visible }; break;
            default: throw new ArgumentException("Unknown terminal input.");
        }
        if (input is TerminalInput.Key key) p.WindowsText=key.WindowsText;
        int size=p.CalculateSize();
        if (size > MaximumBytes) throw new InvalidDataException("Input record exceeds limit.");
        var result = new byte[size+4]; BinaryPrimitives.WriteInt32LittleEndian(result,size);
        p.WriteTo(result.AsSpan(4)); return result;
    }
    public static async ValueTask<(TerminalInput Input, ulong Sequence)> ReadAsync(Stream stream, string incarnation,
        ulong epoch, ulong previous, CancellationToken cancellation)
    {
        byte[] prefix=new byte[4]; await stream.ReadExactlyAsync(prefix,cancellation).ConfigureAwait(false);
        int length=BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 1 or > MaximumBytes) throw new InvalidDataException("Invalid input record length.");
        byte[] payload=GC.AllocateUninitializedArray<byte>(length);
        await stream.ReadExactlyAsync(payload,cancellation).ConfigureAwait(false);
        return Decode(PresentationInput.Parser.ParseFrom(payload),incarnation,epoch,previous);
    }
    internal static (TerminalInput Input, ulong Sequence) Decode(PresentationInput p, string incarnation, ulong epoch, ulong previous)
    {
        if (p.Incarnation != incarnation || p.AttachmentEpoch != epoch || epoch == 0 ||
            previous == ulong.MaxValue || p.Sequence != previous+1 || p.Text.Length > MaximumBytes/2 ||
            p.Raw.Length > MaximumBytes || p.WindowsText.Length > 128 || p.Action > 2 || p.VirtualKey > 255 || p.WindowsUnicode is < 0 or > 65535 ||
            p.ScanCode > 65535 || !float.IsFinite(p.X) || !float.IsFinite(p.Y))
            throw new InvalidDataException("Invalid input identity, order or payload.");
        if(p.Kind == 1 && System.Text.Encoding.UTF8.GetByteCount(p.Text) > 4096)
            throw new InvalidDataException("Key text exceeds the native encoder limit.");
        TerminalInput input = p.Kind switch
        {
            9 => DecodeCommand(p.OwnerCommand),
            1 => new TerminalInput.Key(p.VirtualKey,p.Action,p.Modifiers,p.Text,p.Unshifted,p.ConsumedModifiers,
                p.WindowsUnicode,p.WindowsControlState,p.ScanCode,p.WindowsText),
            2 => new TerminalInput.Text(p.Text),
            3 => new TerminalInput.Paste(p.Text),
            4 when p.CellWidth is > 0 and <= 65535 && p.CellHeight is > 0 and <= 65535 &&
                (!p.HasCellPosition || (p.CellColumn < 10000 && p.CellRow < 10000)) =>
                new TerminalInput.Mouse(p.Action,p.Button,p.Modifiers,p.X,p.Y,p.CellWidth,p.CellHeight,
                    p.HasCellPosition ? (int)p.CellColumn : -1,p.HasCellPosition ? (int)p.CellRow : -1),
            5 => new TerminalInput.Focus(p.Focused),
            6 => new TerminalInput.Raw(p.Raw.ToByteArray()),
            7 when p.FrameSequence > 0 => new TerminalInput.FramePresented(p.FrameSequence),
            8 when p.Viewport is { } v && v.Cols <= int.MaxValue && v.Rows <= int.MaxValue && v.ScrollOffset <= int.MaxValue &&
                v.CellWidth <= int.MaxValue && v.CellHeight <= int.MaxValue =>
                new TerminalInput.Viewport(v.Revision,(int)v.Cols,(int)v.Rows,(int)v.CellWidth,(int)v.CellHeight,(int)v.ScrollOffset,v.Visible),
            _ => throw new InvalidDataException("Unsupported input operation.")
        };
        if(input is TerminalInput.Viewport viewport) ValidateViewport(viewport);
        return (input,p.Sequence);
    }
    private static TerminalInput DecodeCommand(PresentationOwnerCommand? command)
        => new TerminalInput.Command(command switch {
            { Kind:1 } c when c.Bytes.Length <= MaximumBytes-256 => new TerminalCommand.InjectOutput(c.Bytes.ToByteArray()),
            { Kind:2 } c => new TerminalCommand.Clear(c.History),
            { Kind:3 } => new TerminalCommand.Reset(),
            { Kind:4,Rows:>=0 } c => new TerminalCommand.ScrollbackLimit(c.Rows),
            _ => throw new InvalidDataException("Invalid owner command.") });

    private static void ValidateViewport(TerminalInput.Viewport v)
    {
        if(v.Revision == 0 || v.Columns is < 1 or > 10000 || v.Rows is < 1 or > 10000 ||
            (long)v.Columns*v.Rows > Agwinterm.Core.TerminalFrame.MaximumCells ||
            v.CellWidth is < 1 or > 65535 || v.CellHeight is < 1 or > 65535 || v.ScrollOffset < 0)
            throw new InvalidDataException("Invalid presentation viewport.");
    }
}
