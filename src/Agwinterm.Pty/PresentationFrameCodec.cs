using System.Buffers.Binary;
using System.Text;
using Agwinterm.Core;
using Agwinterm.Pty.Proto;
using Google.Protobuf;

namespace Agwinterm.Pty;

/// <summary>Revision 1: protobuf metadata plus changed rows with run-length encoded cells.
/// A delta always names its exact base. Both peers cap records before allocating, and
/// a reader never publishes a partially decoded viewport. No native or VT state crosses.</summary>
internal static class PresentationFrameCodec
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    private const int MaximumStringBytes = 64 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static uint Pack(Color c) => (uint)(c.R << 16 | c.G << 8 | c.B);
    private static Color Unpack(uint c) => new((byte)(c >> 16), (byte)(c >> 8), (byte)c);

    public static byte[] Encode(TerminalFrame.Lease frame, TerminalFrame.Lease? previous = null,
        bool exited = false, int exitCode = 0)
    {
        var s = frame.State; var id = frame.Identity;
        if (previous is not null && (previous.Identity.Incarnation != id.Incarnation ||
            previous.Identity.AttachmentEpoch != id.AttachmentEpoch || previous.Identity.ViewportRevision != id.ViewportRevision ||
            previous.State.Columns != s.Columns || previous.State.Rows != s.Rows)) previous = null;
        var header = new PresentationFrameHeader { Revision = 1, Incarnation = id.Incarnation,
            AttachmentEpoch = id.AttachmentEpoch, Sequence = id.Sequence, BaseSequence = previous?.Identity.Sequence ?? 0,
            ViewportRevision = id.ViewportRevision, TerminalGeneration = id.TerminalGeneration,
            Cols = (uint)s.Columns, Rows = (uint)s.Rows, HistoryRows = (uint)s.HistoryRows, ScrollOffset = (uint)s.ScrollOffset,
            ScrollGeneration = s.ScrollGeneration, CursorCol = (uint)s.CursorColumn, CursorRow = (uint)s.CursorRow,
            CursorVisible = s.CursorVisible, CursorShape = (uint)s.CursorShape, AlternateScreen = s.AlternateScreen,
            Background = Pack(s.Background), CursorColor = Pack(s.CursorColor), Title = s.Title, Cwd = s.WorkingDirectory,
            Modes = s.Modes, KeyboardFlags = s.KeyboardFlags, ScrollTop = (uint)s.ScrollTop, ScrollBottom = (uint)s.ScrollBottom,
            HasExited = exited, ExitCode = exitCode, CapturedTicks = frame.CapturedTicks };
        for(int row=0;row<s.Rows;row++) header.WrapLengths.Add(frame.WrapLength(row));
        foreach(var mark in frame.Marks)
        {
            var encoded=new PresentationCommandMark { PromptLine=mark.PromptLine,CommandLine=mark.CommandLine,
                OutputLine=mark.OutputLine,EndLine=mark.EndLine };
            if(mark.ExitCode is { } code) encoded.ExitCode=code;
            header.Marks.Add(encoded);
        }
        if (frame.Images.Count > 65536) throw new InvalidDataException("Too many image placements.");
        header.Images.AddRange(frame.Images.Select(p => new PresentationImagePlacement {
            Generation=p.Image.Generation,ImageWidth=p.Image.Width,ImageHeight=p.Image.Height,Format=(uint)p.Image.Format,
            PayloadLength=(uint)p.Image.Pixels.Length,
            Id=p.Id,Layer=(uint)p.Layer,Z=p.Z,Column=p.Col,Row=p.Row,OffsetX=p.OffsetX,OffsetY=p.OffsetY,
            Width=p.Width,Height=p.Height,SourceX=p.SourceX,SourceY=p.SourceY,SourceWidth=p.SourceWidth,SourceHeight=p.SourceHeight }));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(0); // outer size, excluding this prefix
        byte[] metadata = header.ToByteArray();
        if (metadata.Length > 8 * 1024 * 1024) throw new InvalidDataException("Presentation metadata exceeds limit.");
        writer.Write(metadata.Length); writer.Write(metadata);
        for (int row = 0; row < s.Rows; row++)
        {
            bool changed = previous is null;
            for (int col = 0; !changed && col < s.Columns; col++) changed = frame[row,col] != previous![row,col];
            if (!changed) continue;
            writer.Write(row);
            for (int col = 0; col < s.Columns;)
            {
                var cell = frame[row,col]; int count = 1;
                while (col+count < s.Columns && frame[row,col+count] == cell) count++;
                writer.Write(count); WriteCell(writer, cell); col += count;
                if (stream.Length > MaximumBytes) throw new InvalidDataException("Presentation record exceeds limit.");
            }
        }
        writer.Write(-1);
        if (stream.Length-4 > MaximumBytes) throw new InvalidDataException("Presentation record exceeds limit.");
        stream.Position = 0; writer.Write(checked((int)stream.Length-4));
        return stream.ToArray();
    }

    public static async ValueTask<(TerminalFrame Frame, bool Exited, int ExitCode)> ReadAsync(Stream stream,
        TerminalFrame.Lease? previous, string incarnation, ulong epoch, CancellationToken cancellation,
        System.Buffers.ArrayPool<Cell>? pool = null)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellation).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 8 or > MaximumBytes) throw new InvalidDataException("Invalid presentation record length.");
        byte[] payload = GC.AllocateUninitializedArray<byte>(length);
        await stream.ReadExactlyAsync(payload, cancellation).ConfigureAwait(false);
        return Decode(payload, previous, incarnation, epoch,pool);
    }

    internal static (TerminalFrame Frame, bool Exited, int ExitCode) Decode(byte[] payload,
        TerminalFrame.Lease? previous, string incarnation, ulong epoch, System.Buffers.ArrayPool<Cell>? pool = null, int? payloadLength = null)
    {
        int length = payloadLength ?? payload.Length;
        if (length is < 8 or > MaximumBytes || length > payload.Length) throw new InvalidDataException("Invalid presentation record length.");
        using var stream = new MemoryStream(payload, 0, length, writable: false);
        using var reader = new BinaryReader(stream, Utf8);
        int headerSize = reader.ReadInt32();
        if (headerSize < 1 || headerSize > 8 * 1024 * 1024 || headerSize > stream.Length-stream.Position-4)
            throw new InvalidDataException("Invalid presentation header length.");
        var h = PresentationFrameHeader.Parser.ParseFrom(reader.ReadBytes(headerSize));
        if (h.Revision != 1 || h.Incarnation != incarnation || h.AttachmentEpoch != epoch || epoch == 0 ||
            h.Sequence == 0 || h.Sequence > long.MaxValue || h.BaseSequence >= h.Sequence || h.TerminalGeneration < 0 ||
            h.ScrollGeneration < 0 || h.CursorShape > 6 || h.KeyboardFlags > int.MaxValue || h.Modes > 511 ||
            h.Cols is < 1 or > 32768 || h.Rows is < 1 or > 32768 ||
            (ulong)h.Cols*h.Rows > TerminalFrame.MaximumCells || h.HistoryRows > int.MaxValue || h.ScrollOffset > h.HistoryRows ||
            h.CursorCol >= h.Cols || h.CursorRow >= h.Rows || h.ScrollTop >= h.Rows || h.ScrollBottom >= h.Rows ||
            h.Title.Length > MaximumStringBytes || h.Cwd.Length > MaximumStringBytes)
            throw new InvalidDataException("Invalid presentation identity or bounds.");
        int cols = (int)h.Cols, rows = (int)h.Rows;
        if(h.WrapLengths.Count != 0 && (h.WrapLengths.Count != rows || h.WrapLengths.Any(w=>w < -1 || w > cols)))
            throw new InvalidDataException("Invalid frame wrap metadata.");
        if (h.Images.Count > 65536) throw new InvalidDataException("Too many image placements.");
        var images=h.Images.Select(DecodeImagePlacement).ToArray();
        long totalRows=(long)h.HistoryRows+h.Rows;
        if(h.Marks.Count > 4096 || h.Marks.Any(m=>m.PromptLine < 0 || m.PromptLine >= totalRows ||
            m.CommandLine < -1 || m.CommandLine >= totalRows || m.OutputLine < -1 || m.OutputLine >= totalRows ||
            m.EndLine < -1 || m.EndLine >= totalRows)) throw new InvalidDataException("Invalid command marks.");
        var marks=h.Marks.Select(m=>new TerminalCommandMark(m.PromptLine,m.CommandLine,m.OutputLine,m.EndLine,
            m.HasExitCode ? m.ExitCode : null)).ToArray();
        if (h.BaseSequence != 0 && (previous is null || previous.Identity.Incarnation != incarnation ||
            previous.Identity.AttachmentEpoch != epoch || previous.Identity.Sequence != h.BaseSequence ||
            previous.Identity.ViewportRevision != h.ViewportRevision || previous.State.Columns != cols || previous.State.Rows != rows))
            throw new InvalidDataException("Presentation delta base is missing; a full viewport is required.");
        if (previous is not null && (h.Sequence <= previous.Identity.Sequence || h.ViewportRevision < previous.Identity.ViewportRevision))
            throw new InvalidDataException("Presentation sequence moved backwards.");
        int cellCount=checked(cols*rows);
        pool ??= System.Buffers.ArrayPool<Cell>.Shared;
        var cells = pool.Rent(cellCount);
        try
        {
        if (h.BaseSequence != 0) previous!.CopyCells(cells.AsSpan(0,cellCount));
        int lastRow = -1, changedRows = 0;
        while (true)
        {
            int row = reader.ReadInt32();
            if (row == -1) break;
            if (row <= lastRow || row >= rows) throw new InvalidDataException("Invalid changed row order.");
            lastRow = row; changedRows++;
            for (int col = 0; col < cols;)
            {
                int count = reader.ReadInt32();
                if (count <= 0 || count > cols-col) throw new InvalidDataException("Invalid cell run length.");
                var cell = ReadCell(reader);
                cells.AsSpan(row*cols+col,count).Fill(cell); col += count;
            }
        }
        if (stream.Position != stream.Length || h.BaseSequence == 0 && changedRows != rows)
            throw new InvalidDataException("Incomplete or trailing presentation data.");
        var state = new TerminalFrameState(cols, rows, (int)h.HistoryRows, (int)h.ScrollOffset, h.ScrollGeneration,
            (int)h.CursorCol, (int)h.CursorRow, h.CursorVisible, (int)h.CursorShape, h.AlternateScreen, false,
            Unpack(h.Background), Unpack(h.CursorColor), h.Title, h.Cwd, h.Modes, h.KeyboardFlags, (int)h.ScrollTop, (int)h.ScrollBottom);
        return (new(new(incarnation,epoch,h.ViewportRevision,h.Sequence,h.TerminalGeneration),state,cells.AsSpan(0,cellCount),h.CapturedTicks,pool,images,
            wrapLengths:h.WrapLengths.Count == 0 ? null : h.WrapLengths,marks:marks),h.HasExited,h.ExitCode);
        }
        finally { pool.Return(cells,clearArray:true); }
    }

    private static GhosttyImagePlacement DecodeImagePlacement(PresentationImagePlacement p)
    {
        int channels=(GhosttyPixelFormat)p.Format switch { GhosttyPixelFormat.Rgb=>3,GhosttyPixelFormat.Rgba or GhosttyPixelFormat.Bgra=>4,
            GhosttyPixelFormat.GrayAlpha=>2,GhosttyPixelFormat.Gray=>1,GhosttyPixelFormat.Png=>1,_=>0 };
        if (p.Generation == 0 || channels == 0 || p.ImageWidth <= 0 || p.ImageHeight <= 0 ||
            (long)p.ImageWidth*p.ImageHeight > 16*1024*1024 || (long)p.ImageWidth*p.ImageHeight*channels > 64*1024*1024 ||
            p.PayloadLength > 64*1024*1024 || ((GhosttyPixelFormat)p.Format == GhosttyPixelFormat.Png && p.PayloadLength < 33) ||
            (p.PayloadLength != 0 && (GhosttyPixelFormat)p.Format != GhosttyPixelFormat.Png && p.PayloadLength != (long)p.ImageWidth*p.ImageHeight*channels) ||
            p.Layer > 2 || p.Width == 0 || p.Height == 0 || p.Width > 1_000_000 || p.Height > 1_000_000 ||
            (ulong)p.SourceX+p.SourceWidth > (uint)p.ImageWidth || (ulong)p.SourceY+p.SourceHeight > (uint)p.ImageHeight)
            throw new InvalidDataException("Invalid image placement.");
        return new(new(p.Generation,p.ImageWidth,p.ImageHeight,(GhosttyPixelFormat)p.Format,ReadOnlyMemory<byte>.Empty,(int)p.PayloadLength),
            p.Id,(GhosttyImageLayer)p.Layer,p.Z,p.Column,p.Row,p.OffsetX,p.OffsetY,p.Width,p.Height,
            p.SourceX,p.SourceY,p.SourceWidth,p.SourceHeight);
    }

    private static void WriteString(BinaryWriter w, string? s)
    {
        if (s is null) { w.Write(-1); return; }
        int count = Utf8.GetByteCount(s);
        if (count > MaximumStringBytes) throw new InvalidDataException("Presentation string exceeds limit.");
        w.Write(count); w.Write(Utf8.GetBytes(s));
    }
    private static string? ReadString(BinaryReader r)
    {
        int count = r.ReadInt32();
        if (count == -1) return null;
        if (count < 0 || count > MaximumStringBytes || count > r.BaseStream.Length-r.BaseStream.Position)
            throw new InvalidDataException("Invalid presentation string length.");
        try { return Utf8.GetString(r.ReadBytes(count)); }
        catch(DecoderFallbackException ex) { throw new InvalidDataException("Invalid presentation UTF-8.",ex); }
    }
    private static void WriteSpec(BinaryWriter w, ColorSpec s) { w.Write((byte)s.Kind); w.Write(s.Index); w.Write(Pack(s.Rgb)); }
    private static ColorSpec ReadSpec(BinaryReader r)
    {
        var kind = (ColorSpecKind)r.ReadByte(); byte index = r.ReadByte(); uint rgb = r.ReadUInt32();
        if (kind > ColorSpecKind.Rgb) throw new InvalidDataException("Invalid color kind.");
        return new(kind,index,Unpack(rgb));
    }
    private static void WriteCell(BinaryWriter w, Cell c)
    {
        w.Write(c.Rune); w.Write(Pack(c.Foreground)); w.Write(Pack(c.Background)); w.Write((uint)c.Attributes); w.Write(c.Width);
        WriteSpec(w,c.FgSpec); WriteSpec(w,c.BgSpec); WriteString(w,c.Grapheme);
        WriteString(w,c.Hyperlink?.Uri); if (c.Hyperlink is not null) WriteString(w,c.Hyperlink.Parameters);
    }
    private static Cell ReadCell(BinaryReader r)
    {
        int rune = r.ReadInt32(); var fg = Unpack(r.ReadUInt32()); var bg = Unpack(r.ReadUInt32());
        var attributes = (CellAttributes)r.ReadUInt32(); byte width = r.ReadByte();
        if (!Rune.IsValid(rune) || width > 2 || ((uint)attributes & ~63u) != 0) throw new InvalidDataException("Invalid presentation cell.");
        var fgSpec = ReadSpec(r); var bgSpec = ReadSpec(r); string? grapheme = ReadString(r), uri = ReadString(r);
        var link = uri is null ? null : new TerminalHyperlink(uri, ReadString(r) ?? "");
        return new(rune,fg,bg,attributes,width,fgSpec,bgSpec,link,grapheme);
    }
}
