using System.Buffers.Binary;
using Agwinterm.Pty.Proto;

namespace Agwinterm.Pty;

/// <summary>Dedicated input reader: one overlapped read fills a reusable buffer with
/// as many complete records as the pipe already has. Small keys do not allocate a
/// prefix/payload array or schedule two async continuations each.</summary>
internal sealed class PresentationInputReader(Stream stream, Func<Memory<byte>,int>? directRead = null)
{
    private readonly byte[] _buffer=new byte[64*1024];
    private int _start,_end;
    public bool HasBufferedRecord
    {
        get
        {
            if(_end-_start < 4) return false;
            int length=BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_start,4));
            if(length is < 1 or > PresentationInputCodec.MaximumBytes) throw new InvalidDataException("Invalid input record length.");
            return _end-_start-4 >= length;
        }
    }
    public (TerminalInput Input,ulong Sequence) Read(string incarnation,ulong epoch,ulong previous,CancellationToken cancellation)
    {
        Fill(4,cancellation);
        int length=BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_start,4)); _start+=4;
        if(length is < 1 or > PresentationInputCodec.MaximumBytes) throw new InvalidDataException("Invalid input record length.");
        if(length <= _buffer.Length)
        {
            Fill(length,cancellation);
            var message=PresentationInput.Parser.ParseFrom(_buffer.AsSpan(_start,length)); _start+=length;
            return PresentationInputCodec.Decode(message,incarnation,epoch,previous);
        }
        byte[] payload=System.Buffers.ArrayPool<byte>.Shared.Rent(length);
        try
        {
            int copied=_end-_start; _buffer.AsSpan(_start,copied).CopyTo(payload); _start=_end=0;
            while(copied < length)
            {
                int read=ReadBytes(payload.AsMemory(copied,length-copied),cancellation);
                if(read == 0) throw new EndOfStreamException(); copied+=read;
            }
            return PresentationInputCodec.Decode(PresentationInput.Parser.ParseFrom(payload.AsSpan(0,length)),incarnation,epoch,previous);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(payload,clearArray:true); }
    }
    private void Fill(int minimum,CancellationToken cancellation)
    {
        if(_end-_start >= minimum) return;
        int remaining=_end-_start;
        _buffer.AsSpan(_start,remaining).CopyTo(_buffer); _start=0; _end=remaining;
        while(_end < minimum)
        {
            int read=ReadBytes(_buffer.AsMemory(_end),cancellation);
            if(read == 0) throw new EndOfStreamException(); _end+=read;
        }
    }
    private int ReadBytes(Memory<byte> buffer,CancellationToken cancellation)
        => directRead is not null ? directRead(buffer) : stream.ReadAsync(buffer,cancellation).AsTask().GetAwaiter().GetResult();
}
