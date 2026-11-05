using System.Buffers.Binary;

namespace Agwinterm.Pty;

/// <summary>Dedicated journal reader. Payload is borrowed until the next Read;
/// consumers must finish feeding/restoring it before advancing. Large checkpoints
/// have independent storage, but callers must not rely on that implementation detail.</summary>
internal sealed class JournalReader(Stream stream, Func<Memory<byte>,int>? directRead = null)
{
    private readonly byte[] _buffer=new byte[TerminalJournal.MaximumOutputBytes+22];
    private int _start,_end;

    public JournalMessage Read(ulong expectedEpoch,CancellationToken cancellation)
    {
        Fill(4,cancellation);
        int length=BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_start));
        if(length < JournalWire.HeaderBytes || length > JournalWire.HeaderBytes+TerminalJournal.MaximumCheckpointBytes)
            throw new InvalidDataException("Invalid journal record length.");
        Fill(22,cancellation);
        var header=_buffer.AsSpan(_start,22);
        if(header[4] != 1) throw new InvalidDataException("Unsupported journal framing revision.");
        var kind=(JournalMessageKind)header[5];
        ulong epoch=BinaryPrimitives.ReadUInt64LittleEndian(header[6..]);
        long sequence=BinaryPrimitives.ReadInt64LittleEndian(header[14..]);
        if(epoch != expectedEpoch) throw new InvalidDataException("Stale journal attachment.");
        int size=length-JournalWire.HeaderBytes;
        JournalWire.Validate(kind,epoch,sequence,size);
        _start+=22;
        if(size <= _buffer.Length)
        {
            Fill(size,cancellation);
            var payload=_buffer.AsMemory(_start,size); _start+=size;
            return new(kind,epoch,sequence,payload);
        }
        byte[] owned=GC.AllocateUninitializedArray<byte>(size);
        int copied=_end-_start;
        _buffer.AsSpan(_start,copied).CopyTo(owned); _start=_end=0;
        while(copied < size)
        {
            int read=ReadBytes(owned.AsMemory(copied),cancellation);
            if(read == 0) throw new EndOfStreamException();
            copied+=read;
        }
        return new(kind,epoch,sequence,owned);
    }
    private void Fill(int minimum,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if(_end-_start >= minimum) return;
        int remaining=_end-_start;
        _buffer.AsSpan(_start,remaining).CopyTo(_buffer); _start=0; _end=remaining;
        while(_end < minimum)
        {
            int read=ReadBytes(_buffer.AsMemory(_end),cancellation);
            if(read == 0) throw new EndOfStreamException();
            _end+=read;
        }
    }
    private int ReadBytes(Memory<byte> buffer,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return directRead is not null ? directRead(buffer) : stream.ReadAsync(buffer,cancellation).AsTask().GetAwaiter().GetResult();
    }
}
