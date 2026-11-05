using System.Buffers.Binary;
using Xunit;

namespace Agwinterm.Pty.Tests;

public class JournalWireTests
{
    [Fact]
    public async Task BatchedRecordsRetainBoundariesKindsAndPositions()
    {
        var batch=new System.Buffers.ArrayBufferWriter<byte>();
        byte[] output=Enumerable.Range(0,65536).Select(n=>(byte)n).ToArray();
        JournalWire.Append(batch,new(JournalMessageKind.Output,7,9,output));
        JournalWire.Append(batch,new(JournalMessageKind.Resize,7,10,new byte[8]));
        JournalWire.Append(batch,new(JournalMessageKind.Output,7,11,new byte[]{27,91}));
        using var stream=new MemoryStream(batch.WrittenSpan.ToArray());
        var first=await JournalWire.ReadAsync(stream,7,default);
        Assert.Equal(9,first.Sequence); Assert.Equal(output,first.Payload.ToArray());
        Assert.Equal(JournalMessageKind.Resize,(await JournalWire.ReadAsync(stream,7,default)).Kind);
        var last=await JournalWire.ReadAsync(stream,7,default);
        Assert.Equal(11,last.Sequence); Assert.Equal(new byte[]{27,91},last.Payload.ToArray());
        Assert.Equal(stream.Length,stream.Position);
    }
    [Fact]
    public async Task RecordsPreserveExactBytesAndIndependentSequence()
    {
        using var stream=new MemoryStream(JournalWire.Encode(new(JournalMessageKind.Output,2,123,new byte[]{0,27,255})));
        var actual=await JournalWire.ReadAsync(stream,2,default);
        Assert.Equal(123,actual.Sequence); Assert.Equal(new byte[]{0,27,255},actual.Payload.ToArray());
    }
    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(int.MaxValue)]
    public async Task InvalidLengthIsRejectedBeforeAllocation(int length)
    {
        byte[] prefix=new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix,length);
        using var stream=new MemoryStream(prefix);
        await Assert.ThrowsAsync<InvalidDataException>(async()=>await JournalWire.ReadAsync(stream,1,default));
    }
    [Fact]
    public async Task StaleEpochAndPartialRecordAreRejected()
    {
        byte[] bytes=JournalWire.Encode(new(JournalMessageKind.Output,1,1,new byte[]{1,2}));
        using var stale=new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(async()=>await JournalWire.ReadAsync(stale,2,default));
        using var partial=new MemoryStream(bytes[..^1]);
        await Assert.ThrowsAsync<EndOfStreamException>(async()=>await JournalWire.ReadAsync(partial,1,default));
    }
    [Fact]
    public void ReplacementRevokesInputAndDeduplicatesRepliesAcrossReplay()
    {
        var fence=new JournalInputFence(); ulong first=fence.Attach();
        fence.AcceptInput(first,1);
        Assert.True(fence.AcceptResponse(first,4,0,4));
        ulong second=fence.Attach();
        Assert.Throws<InvalidDataException>(()=>fence.AcceptInput(first,2));
        Assert.False(fence.AcceptResponse(second,4,0,5));
        Assert.True(fence.AcceptResponse(second,4,1,5));
        Assert.Throws<InvalidDataException>(()=>fence.AcceptInput(second,2));
        fence.AcceptInput(second,1);
        fence.Checkpoint(4);
        Assert.False(fence.AcceptResponse(second,4,2,5));
        fence.Revoke();
        Assert.Throws<InvalidDataException>(()=>fence.AcceptResponse(second,5,0,5));
    }
}
