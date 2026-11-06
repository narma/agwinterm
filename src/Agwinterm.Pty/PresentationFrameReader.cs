using System.Buffers.Binary;
using Agwinterm.Core;

namespace Agwinterm.Pty;

/// <summary>Dedicated presentation reader. Reuses encoded storage; decoded frames
/// own their cells and strings and never borrow the next record's buffer.</summary>
internal sealed class PresentationFrameReader(Stream stream, Func<Memory<byte>, int>? directRead = null)
{
    private readonly byte[] _prefix = new byte[4];
    private byte[] _payload = new byte[64 * 1024];

    public (TerminalFrame Frame, bool Exited, int ExitCode) Read(TerminalFrame.Lease? previous,
        string incarnation, ulong epoch, CancellationToken cancellation,
        System.Buffers.ArrayPool<Cell>? pool = null)
    {
        ReadExactly(_prefix, cancellation);
        int length = BinaryPrimitives.ReadInt32LittleEndian(_prefix);
        if (length is < 8 or > PresentationFrameCodec.MaximumBytes)
            throw new InvalidDataException("Invalid presentation record length.");
        // Do not retain an exceptional multi-megabyte viewport forever after resize.
        byte[] payload = length <= _payload.Length ? _payload :
            length <= 1024 * 1024 ? _payload = new byte[length] : new byte[length];
        ReadExactly(payload.AsMemory(0, length), cancellation);
        return PresentationFrameCodec.Decode(payload, previous, incarnation, epoch, pool, length);
    }

    private void ReadExactly(Memory<byte> bytes, CancellationToken cancellation)
    {
        while (!bytes.IsEmpty)
        {
            cancellation.ThrowIfCancellationRequested();
            int count = directRead is null
                ? stream.ReadAsync(bytes, cancellation).AsTask().GetAwaiter().GetResult()
                : directRead(bytes);
            if (count == 0) throw new EndOfStreamException();
            bytes = bytes[count..];
        }
    }
}
