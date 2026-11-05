using Agwinterm.Pty.Proto;
using Xunit;

namespace Agwinterm.Pty.Tests;

public class PresentationInputCodecTests
{
    [Fact]
    public async Task MouseCarriesBothPixelAndCellPositionsForOwnerModeSelection()
    {
        var expected=new TerminalInput.Mouse(0,1,0,31,47,9,19,3,2);
        using var stream=new MemoryStream(PresentationInputCodec.Encode(expected,"owner",1,1));
        Assert.Equal(expected,(await PresentationInputCodec.ReadAsync(stream,"owner",1,0,default)).Input);
    }
    [Fact]
    public async Task ViewportRoundTripsWithItsGenerationAndPhysicalCellSize()
    {
        var expected=new TerminalInput.Viewport(12,120,40,9,19,500,true);
        using var stream=new MemoryStream(PresentationInputCodec.Encode(expected,"owner",2,1));
        var actual=await PresentationInputCodec.ReadAsync(stream,"owner",2,0,default);
        Assert.Equal(expected,actual.Input);
    }
    [Theory]
    [InlineData(0,80,24,9,19,0)]
    [InlineData(1,10000,10000,9,19,0)]
    [InlineData(1,80,24,0,19,0)]
    [InlineData(1,80,24,9,65536,0)]
    [InlineData(1,80,24,9,19,-1)]
    public void ViewportBoundsAreValidatedBeforeSending(int revision,int cols,int rows,int width,int height,int scroll)
        => Assert.Throws<InvalidDataException>(()=>PresentationInputCodec.Encode(new TerminalInput.Viewport((ulong)revision,cols,rows,width,height,scroll,true),"owner",1,1));

    [Fact]
    public void OversizedUtf8KeyIsRejectedBeforeItCanFaultTheOwnerWriter()
    {
        string text=new('é',2049);
        var key=new TerminalInput.Key(0,1,0,text,0,0,0,0,0);
        Assert.Throws<InvalidDataException>(()=>PresentationInputCodec.Encode(key,"owner",1,1));
        var message=new PresentationInput { Incarnation="owner",AttachmentEpoch=1,Sequence=1,Kind=1,Text=text };
        Assert.Throws<InvalidDataException>(()=>PresentationInputCodec.Decode(message,"owner",1,0));
    }
    [Fact]
    public async Task InputRoundTripsWithoutChangingWindowsTranslation()
    {
        TerminalInput expected=new TerminalInput.Key(65,2,6,"é",97,0,0,10,30,"´a");
        using var stream=new MemoryStream(PresentationInputCodec.Encode(expected,"owner",2,1));
        var actual=await PresentationInputCodec.ReadAsync(stream,"owner",2,0,default);
        Assert.Equal(expected,actual.Input); Assert.Equal(1ul,actual.Sequence);
    }
    [Theory]
    [InlineData("replacement",1ul,1ul)]
    [InlineData("owner",2ul,1ul)]
    [InlineData("owner",1ul,0ul)]
    [InlineData("owner",1ul,2ul)]
    public void IdentityAndExactOrderAreRequired(string incarnation,ulong epoch,ulong sequence)
    {
        var p=new PresentationInput { Incarnation=incarnation,AttachmentEpoch=epoch,Sequence=sequence,Kind=5 };
        Assert.Throws<InvalidDataException>(()=>PresentationInputCodec.Decode(p,"owner",1,0));
    }
    [Fact]
    public async Task RawBytesAreNeverReencoded()
    {
        byte[] bytes=[0,27,255,13,10];
        using var stream=new MemoryStream(PresentationInputCodec.Encode(new TerminalInput.Raw(bytes),"owner",1,1));
        var result=await PresentationInputCodec.ReadAsync(stream,"owner",1,0,default);
        Assert.Equal(bytes,Assert.IsType<TerminalInput.Raw>(result.Input).Bytes.ToArray());
    }
    [Fact]
    public void InvalidMouseCoordinatesAreRejected()
    {
        var p=new PresentationInput { Incarnation="owner",AttachmentEpoch=1,Sequence=1,Kind=4,CellWidth=8,CellHeight=18,X=float.NaN };
        Assert.Throws<InvalidDataException>(()=>PresentationInputCodec.Decode(p,"owner",1,0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(65536)]
    public void BufferedReaderHandlesPartialAndCombinedRecords(int maximumRead)
    {
        var records=Enumerable.Range(1,25).SelectMany(i=>PresentationInputCodec.Encode(new TerminalInput.Text("text"+i),"owner",1,(ulong)i)).ToArray();
        using var stream=new ShortReadStream(records,maximumRead);
        var reader=new PresentationInputReader(stream);
        for(ulong i=0;i<25;i++) Assert.Equal(new TerminalInput.Text("text"+(i+1)),reader.Read("owner",1,i,default).Input);
        Assert.Throws<EndOfStreamException>(()=>reader.Read("owner",1,25,default));
    }
    private sealed class ShortReadStream(byte[] bytes,int maximum) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellation=default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length,maximum)],cancellation);
    }
}
