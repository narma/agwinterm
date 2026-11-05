using System.Buffers.Binary;

namespace Agwinterm.Pty;

internal enum JournalMessageKind : byte
{ Output=1, Resize=2, Checkpoint=3, Live=4, Exit=5, Input=16, Response=17, ResizeRequest=18, InjectRequest=19 }
internal sealed record JournalMessage(JournalMessageKind Kind,ulong Epoch,long Sequence,ReadOnlyMemory<byte> Payload);

/// <summary>Versioned framing only; no VT parsing. Each direction has an independent pipe.</summary>
internal static class JournalWire
{
    private const int HeaderBytes=18;
    public static byte[] Encode(JournalMessage message)
    {
        Validate(message.Kind,message.Epoch,message.Sequence,message.Payload.Length);
        byte[] bytes=new byte[4+HeaderBytes+message.Payload.Length];
        EncodeInto(bytes,message);
        return bytes;
    }
    public static void Append(System.Buffers.IBufferWriter<byte> writer,JournalMessage message)
    {
        Validate(message.Kind,message.Epoch,message.Sequence,message.Payload.Length);
        int size=4+HeaderBytes+message.Payload.Length;
        EncodeInto(writer.GetSpan(size)[..size],message); writer.Advance(size);
    }
    private static void EncodeInto(Span<byte> bytes,JournalMessage message)
    {
        BinaryPrimitives.WriteInt32LittleEndian(bytes,HeaderBytes+message.Payload.Length);
        bytes[4]=1; bytes[5]=(byte)message.Kind;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[6..],message.Epoch);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[14..],message.Sequence);
        message.Payload.Span.CopyTo(bytes[22..]);
    }
    public static async ValueTask<JournalMessage> ReadAsync(Stream stream,ulong expectedEpoch,CancellationToken cancellation)
    {
        byte[] header=new byte[4+HeaderBytes];
        await stream.ReadExactlyAsync(header.AsMemory(0,4),cancellation).ConfigureAwait(false);
        int length=BinaryPrimitives.ReadInt32LittleEndian(header);
        if(length < HeaderBytes || length > HeaderBytes+TerminalJournal.MaximumCheckpointBytes)
            throw new InvalidDataException("Invalid journal record length.");
        await stream.ReadExactlyAsync(header.AsMemory(4),cancellation).ConfigureAwait(false);
        if(header[4] != 1) throw new InvalidDataException("Unsupported journal framing revision.");
        var kind=(JournalMessageKind)header[5];
        ulong epoch=BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(6));
        long sequence=BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(14));
        if(epoch != expectedEpoch) throw new InvalidDataException("Stale journal attachment.");
        Validate(kind,epoch,sequence,length-HeaderBytes);
        byte[] payload=GC.AllocateUninitializedArray<byte>(length-HeaderBytes);
        await stream.ReadExactlyAsync(payload,cancellation).ConfigureAwait(false);
        return new(kind,epoch,sequence,payload);
    }
    private static void Validate(JournalMessageKind kind,ulong epoch,long sequence,int payload)
    {
        bool valid=kind switch
        {
            JournalMessageKind.Output => sequence > 0 && payload is > 0 and <= TerminalJournal.MaximumOutputBytes,
            JournalMessageKind.Resize or JournalMessageKind.ResizeRequest => sequence > 0 && payload == 8,
            JournalMessageKind.Checkpoint => payload <= TerminalJournal.MaximumCheckpointBytes,
            JournalMessageKind.Live => payload == 0,
            JournalMessageKind.Exit => payload == 4,
            JournalMessageKind.Input => sequence > 0 && payload is > 0 and <= 1024*1024,
            JournalMessageKind.InjectRequest => sequence > 0 && payload is > 0 and <= TerminalJournal.MaximumOutputBytes,
            JournalMessageKind.Response => sequence > 0 && payload is > 12 and <= 1024*1024,
            _ => false
        };
        if(!valid || epoch == 0 || sequence < 0) throw new InvalidDataException("Invalid journal message.");
    }
}

/// <summary>Broker-lifetime response deduplication; checkpoints release covered IDs.
/// Caller serializes acceptance and PTY enqueueing with replacement attachment.</summary>
internal sealed class JournalInputFence
{
    private readonly HashSet<(long Sequence,int Index)> _responses=[];
    private ulong _epoch;
    private long _inputSequence, _checkpointSequence;
    public long InputSequence => _inputSequence;
    public ulong Attach() { _inputSequence=0; return _epoch=checked(_epoch+1); }
    public void Revoke() { _epoch=checked(_epoch+1); _inputSequence=0; }
    public void AcceptInput(ulong epoch,long sequence)
    {
        if(epoch != _epoch || _epoch == 0 || sequence != checked(_inputSequence+1)) throw new InvalidDataException("Stale or reordered input.");
        _inputSequence=sequence;
    }
    public bool AcceptResponse(ulong epoch,long eventSequence,int responseIndex,long journalTail)
    {
        if(epoch != _epoch || _epoch == 0 || eventSequence <= 0 || eventSequence > journalTail || responseIndex is < 0 or >= 16384)
            throw new InvalidDataException("Invalid terminal response identity.");
        if(eventSequence <= _checkpointSequence || _responses.Contains((eventSequence,responseIndex))) return false;
        if(_responses.Count >= 65536) throw new IOException("Terminal response retention limit reached; checkpoint required.");
        return _responses.Add((eventSequence,responseIndex));
    }
    public void Checkpoint(long sequence)
    {
        if(sequence < _checkpointSequence) throw new ArgumentOutOfRangeException(nameof(sequence));
        _checkpointSequence=sequence;
        _responses.RemoveWhere(item=>item.Sequence <= sequence);
    }
}
