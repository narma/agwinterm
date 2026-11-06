using System.Buffers.Binary;
using Agwinterm.Core;
using Xunit;

namespace Agwinterm.Pty.Tests;

public class PresentationFrameReaderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(65536)]
    public void FragmentedFullAndDeltaKeepRetainedFrameIndependent(int fragment)
    {
        var state = new TerminalFrameState(2,1,0,0,0,0,0,true,1,false,false,default,default,"","");
        using var first = new TerminalFrame(new("owner",1,0,1,1),state,
            [Cell.Empty with { Grapheme="中\u0301", Rune=0x4e2d },Cell.Empty]);
        using var second = new TerminalFrame(new("owner",1,0,2,2),state,
            [Cell.Empty,Cell.Empty with { Rune='Z' }]);
        using var a = first.Acquire(); using var b = second.Acquire();
        using var stream = new MemoryStream([..PresentationFrameCodec.Encode(a),..PresentationFrameCodec.Encode(b,a,true,7)]);
        var reader = new PresentationFrameReader(stream, bytes => stream.Read(bytes.Span[..Math.Min(bytes.Length,fragment)]));
        using var decodedA = reader.Read(null,"owner",1,default).Frame;
        using var held = decodedA.Acquire();
        var next = reader.Read(held,"owner",1,default);
        using var decodedB = next.Frame; using var fresh = decodedB.Acquire();
        Assert.Equal("中\u0301",held[0,0].Grapheme);
        Assert.Equal('Z',fresh[0,1].Rune);
        Assert.True(next.Exited); Assert.Equal(7,next.ExitCode);
        Assert.Throws<EndOfStreamException>(() => reader.Read(fresh,"owner",1,default));
    }

    [Fact]
    public void RejectsOversizeBeforePayloadAndObservesCancellation()
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix,PresentationFrameCodec.MaximumBytes+1);
        using var stream = new MemoryStream(prefix);
        var reader = new PresentationFrameReader(stream);
        Assert.Throws<InvalidDataException>(() => reader.Read(null,"owner",1,default));
        Assert.Equal(4,stream.Position);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.Read(null,"owner",1,cancelled.Token));
    }

    [Fact]
    public void TruncatedPayloadCannotProduceAFrame()
    {
        using var stream = new MemoryStream([8,0,0,0,1,2,3]);
        var reader = new PresentationFrameReader(stream);
        Assert.Throws<EndOfStreamException>(() => reader.Read(null,"owner",1,default));
    }
}
