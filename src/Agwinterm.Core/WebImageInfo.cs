using System.Buffers.Binary;

namespace Agwinterm.Core;

public sealed record WebImageInfo(string Mime, int Width, int Height)
{
    public const int MaxBytes = 64 * 1024 * 1024;
    public static WebImageInfo Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxBytes) throw new ArgumentException("image too large");
        int w = 0, h = 0; string mime;
        if (data.Length >= 24 && data[..8].SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}) && data.Slice(12,4).SequenceEqual("IHDR"u8))
        { mime = "image/png"; w = BinaryPrimitives.ReadInt32BigEndian(data[16..]); h = BinaryPrimitives.ReadInt32BigEndian(data[20..]); }
        else if (data.Length >= 10 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
        { mime = "image/gif"; w = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]); h = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]); }
        else if (data.Length >= 30 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8,4).SequenceEqual("WEBP"u8))
        {
            mime = "image/webp";
            if (data.Slice(12,4).SequenceEqual("VP8X"u8)) { w = 1 + data[24] + (data[25]<<8) + (data[26]<<16); h = 1 + data[27] + (data[28]<<8) + (data[29]<<16); }
            else if (data.Slice(12,4).SequenceEqual("VP8L"u8) && data[20] == 0x2f) { uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..]); w = (int)(bits & 0x3fff)+1; h = (int)((bits>>14)&0x3fff)+1; }
            else if (data.Slice(12,4).SequenceEqual("VP8 "u8) && data.Slice(23,3).SequenceEqual(new byte[] {0x9d,1,0x2a})) { w = BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & 0x3fff; h = BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & 0x3fff; }
        }
        else if (data.Length >= 4 && data[0] == 0xff && data[1] == 0xd8)
        {
            mime = "image/jpeg";
            for (int offset = 2; offset + 4 <= data.Length;)
            {
                if (data[offset++] != 0xff) break;
                while (offset < data.Length && data[offset] == 0xff) offset++;
                if (offset >= data.Length) break;
                byte marker = data[offset++];
                if (marker is 0xd9 or 0xda) break;
                if (marker is >= 0xd0 and <= 0xd7 or 0x01) continue;
                if (offset + 2 > data.Length) break;
                int length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
                if (length < 2 || length > data.Length - offset) break;
                if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc) && length >= 8)
                { h = BinaryPrimitives.ReadUInt16BigEndian(data[(offset+3)..]); w = BinaryPrimitives.ReadUInt16BigEndian(data[(offset+5)..]); break; }
                offset += length;
            }
        }
        else throw new ArgumentException("not a png, jpg, gif or webp image");
        if (w <= 0 || h <= 0) throw new ArgumentException("unreadable image dimensions");
        if ((long)w * h > 100_000_000) throw new ArgumentException("image too large");
        return new(mime, w, h);
    }
}
