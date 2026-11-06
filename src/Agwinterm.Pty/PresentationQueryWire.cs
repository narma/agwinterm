using System.Buffers.Binary;
using Google.Protobuf;

namespace Agwinterm.Pty;

internal static class PresentationQueryWire
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal static async Task<T> ReadAsync<T>(Stream stream, MessageParser<T> parser, CancellationToken cancellation)
        where T : IMessage<T>
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix,cancellation).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 1 or > MaximumBytes) throw new InvalidDataException("Invalid owner query record length.");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload,cancellation).ConfigureAwait(false);
        return parser.ParseFrom(payload);
    }
    internal static async Task WriteAsync(Stream stream, IMessage message, CancellationToken cancellation)
    {
        int length = message.CalculateSize();
        if (length is < 1 or > MaximumBytes) throw new InvalidDataException("Owner query result exceeds limit.");
        byte[] payload = new byte[4+length];
        BinaryPrimitives.WriteInt32LittleEndian(payload,length);
        message.WriteTo(payload.AsSpan(4));
        await stream.WriteAsync(payload,cancellation).ConfigureAwait(false);
    }
}
