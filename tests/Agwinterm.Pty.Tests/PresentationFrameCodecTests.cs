using System.Buffers.Binary;
using Agwinterm.Core;
using Xunit;

namespace Agwinterm.Pty.Tests;

public class PresentationFrameCodecTests
{
    [Fact]
    public void BlinkOnlyDeltaKeepsOldSnapshotAndRejectsPreBlinkFrames()
    {
        var cell=Cell.Empty with { Rune='X', Attributes=CellAttributes.Blink|CellAttributes.Underline };
        using var first=Frame(1,cell); using var a=first.Acquire();
        using var decoded=Decode(PresentationFrameCodec.Encode(a)).Frame; using var old=decoded.Acquire();
        Assert.Equal(cell,old[0,0]);
        using var second=Frame(2,cell with {Attributes=CellAttributes.Underline}); using var b=second.Acquire();
        using var delta=Decode(PresentationFrameCodec.Encode(b,a),old).Frame; using var current=delta.Acquire();
        Assert.Equal(CellAttributes.Underline,current[0,0].Attributes);
        Assert.True(old[0,0].Attributes.HasFlag(CellAttributes.Blink));
        var bytes=PresentationFrameCodec.Encode(a); bytes[9]=2;
        Assert.Throws<InvalidDataException>(()=>Decode(bytes));
    }
    [Fact]
    public void DecorationOnlyDeltaPreservesOldFrameAndRejectsOldRevision()
    {
        var cell=Cell.Empty with { Attributes=CellAttributes.Underline|CellAttributes.Overline,
            UnderlineStyle=UnderlineStyle.Curly, UnderlineColor=new(11,22,33), UnderlineSpec=ColorSpec.Indexed(42) };
        using var first=Frame(1,cell); using var a=first.Acquire();
        using var decoded=Decode(PresentationFrameCodec.Encode(a)).Frame; using var old=decoded.Acquire();
        Assert.Equal(cell,old[0,0]);
        var replacement=cell with { UnderlineStyle=UnderlineStyle.Double, UnderlineColor=new(44,55,66) };
        using var second=Frame(2,replacement); using var b=second.Acquire();
        using var delta=Decode(PresentationFrameCodec.Encode(b,a),old).Frame; using var current=delta.Acquire();
        Assert.Equal(replacement,current[0,0]); Assert.Equal(cell,old[0,0]);
        var bytes=PresentationFrameCodec.Encode(a);
        // The first protobuf header field is the row-encoding revision.
        Assert.Equal(8,bytes[8]); bytes[9]=1;
        Assert.Throws<InvalidDataException>(()=>Decode(bytes));
    }
    [Fact]
    public void CommandMarksAndEncodedImageLengthSurviveTransport()
    {
        using var text=Frame(1,Cell.Empty);
        var image=new GhosttyImage(42,1,1,GhosttyPixelFormat.Png,new byte[67]);
        var placement=new GhosttyImagePlacement(image,7,GhosttyImageLayer.AboveText,0,0,0,0,0,1,1,0,0,1,1);
        var mark=new TerminalCommandMark(0,0,0,-1,null);
        using var frame=new TerminalFrame(text.Identity,text.State,[Cell.Empty],images:[placement],marks:[mark]);
        using var lease=frame.Acquire(); using var decoded=Decode(PresentationFrameCodec.Encode(lease)).Frame;
        Assert.Equal(mark,Assert.Single(decoded.Marks));
        Assert.Equal(67,Assert.Single(decoded.Images).Image.PayloadLength);
    }
    [Fact]
    public void ImageMetadataTravelsWithoutCopyingPixelPayloadIntoFramePipe()
    {
        using var text=Frame(1,Cell.Empty);
        var image=new GhosttyImage(42,2,1,GhosttyPixelFormat.Rgba,new byte[8]);
        var placement=new GhosttyImagePlacement(image,7,GhosttyImageLayer.AboveText,1,0,0,0,0,2,1,0,0,2,1);
        using var frame=new TerminalFrame(text.Identity,text.State,[Cell.Empty],images:[placement]);
        using var lease=frame.Acquire();
        using var decoded=Decode(PresentationFrameCodec.Encode(lease)).Frame;
        using var read=decoded.Acquire();
        var actual=Assert.Single(read.Images);
        Assert.Equal(42ul,actual.Image.Generation);
        Assert.Equal(2,actual.Image.Width);
        Assert.True(actual.Image.Pixels.IsEmpty);
        Assert.Equal(GhosttyImageLayer.AboveText,actual.Layer);
    }

    private static TerminalFrame Frame(ulong sequence, params Cell[] cells) => new(new("owner",1,0,sequence,(long)sequence),
        new(cells.Length,1,0,0,0,0,0,true,1,false,false,default,default,"title","cwd"),cells);
    private static (TerminalFrame Frame, bool Exited, int ExitCode) Decode(byte[] bytes, TerminalFrame.Lease? previous = null)
        => PresentationFrameCodec.Decode(bytes[4..],previous,"owner",1);

    [Fact]
    public void FullAndDeltaKeepGraphemesLinksStylesAndOldLease()
    {
        var styled = Cell.Empty with { Rune = 0x4e2d, Width = 2, Grapheme = "中\u0301", Attributes = CellAttributes.Bold,
            Foreground = new(1,2,3), FgSpec = ColorSpec.Indexed(12), Hyperlink = new("https://example.test","id=1") };
        using var first = Frame(1,styled,Cell.Empty with {Width=0},Cell.Empty);
        using var firstLease = first.Acquire();
        var read = Decode(PresentationFrameCodec.Encode(firstLease));
        using var decoded = read.Frame; using var old = decoded.Acquire();
        Assert.Equal(styled,old[0,0]);
        using var next = Frame(2,styled,Cell.Empty with {Width=0},Cell.Empty with {Rune='Z'});
        using var nextLease = next.Acquire();
        byte[] delta = PresentationFrameCodec.Encode(nextLease,firstLease,true,7);
        var result = Decode(delta,old);
        using var changed = result.Frame; using var changedLease = changed.Acquire();
        Assert.Equal('Z',changedLease[0,2].Rune); Assert.Equal(' ',old[0,2].Rune);
        Assert.True(result.Exited); Assert.Equal(7,result.ExitCode);
        Assert.Throws<InvalidDataException>(() => Decode(delta));
    }

    [Fact]
    public void ChangedViewportRevisionForcesAFullFrameWithoutTheOldBase()
    {
        using var first=Frame(1,Cell.Empty);
        using var old=first.Acquire();
        using var next=new TerminalFrame(first.Identity with { Sequence=2,ViewportRevision=1 },first.State,[Cell.Empty with { Rune='R' }]);
        using var current=next.Acquire();
        using var decoded=Decode(PresentationFrameCodec.Encode(current,old)).Frame;
        using var lease=decoded.Acquire();
        Assert.Equal(1ul,lease.Identity.ViewportRevision);
        Assert.Equal('R',lease[0,0].Rune);
    }

    [Fact]
    public void UnchangedViewportUsesSmallMetadataOnlyDelta()
    {
        var cells = Enumerable.Repeat(Cell.Empty,120).ToArray();
        using var first = Frame(1,cells); using var a = first.Acquire();
        using var next = Frame(2,cells); using var b = next.Acquire();
        byte[] full = PresentationFrameCodec.Encode(b), delta = PresentationFrameCodec.Encode(b,a);
        Assert.True(delta.Length < full.Length);
        using var decoded = Decode(delta,a).Frame;
        using var lease = decoded.Acquire(); Assert.Equal(Cell.Empty,lease[0,119]);
    }

    [Fact]
    public async Task OversizePrefixIsRejectedBeforeReadingPayload()
    {
        byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix,PresentationFrameCodec.MaximumBytes+1);
        using var stream = new MemoryStream(prefix);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await PresentationFrameCodec.ReadAsync(stream,null,"owner",1,default));
    }

    [Fact]
    public void MalformedLengthsAndFencesNeverPublishPartialFrame()
    {
        using var frame = Frame(1,Cell.Empty); using var lease = frame.Acquire();
        byte[] valid = PresentationFrameCodec.Encode(lease);
        Assert.Throws<InvalidDataException>(() => PresentationFrameCodec.Decode(valid[4..],null,"replacement",1));
        Assert.Throws<InvalidDataException>(() => PresentationFrameCodec.Decode(valid[4..],null,"owner",2));
        byte[] hugeHeader = valid[4..]; BinaryPrimitives.WriteInt32LittleEndian(hugeHeader,int.MaxValue);
        Assert.Throws<InvalidDataException>(() => PresentationFrameCodec.Decode(hugeHeader,null,"owner",1));
        Assert.Throws<InvalidDataException>(() => Decode([.. valid,0]));
        Assert.Throws<EndOfStreamException>(() => Decode(valid[..^1]));
    }
}
