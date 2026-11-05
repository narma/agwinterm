using System.Buffers.Binary;
using Xunit;

namespace Agwinterm.Pty.Tests;

public class JournalReaderTests
{
    [Theory]
    [InlineData(1)] [InlineData(17)] [InlineData(65558)]
    public void FragmentedAndCoalescedRecordsPreserveBytes(int fragment)
    {
        var batch=new System.Buffers.ArrayBufferWriter<byte>();
        var expected=new[] {
            new JournalMessage(JournalMessageKind.Checkpoint,7,8,new byte[100000]),
            new JournalMessage(JournalMessageKind.Live,7,8,ReadOnlyMemory<byte>.Empty),
            new JournalMessage(JournalMessageKind.Output,7,9,Enumerable.Range(0,65536).Select(n=>(byte)n).ToArray()),
            new JournalMessage(JournalMessageKind.Resize,7,10,new byte[8]),
            new JournalMessage(JournalMessageKind.Output,7,11,new byte[]{27,91,255}) };
        foreach(var item in expected) JournalWire.Append(batch,item);
        using var stream=new MemoryStream(batch.WrittenSpan.ToArray());
        var reader=new JournalReader(stream,memory=>stream.Read(memory.Span[..Math.Min(fragment,memory.Length)]));
        foreach(var item in expected)
        {
            var actual=reader.Read(7,default);
            Assert.Equal(item.Kind,actual.Kind); Assert.Equal(item.Sequence,actual.Sequence);
            Assert.Equal(item.Payload.ToArray(),actual.Payload.ToArray());
        }
        Assert.Throws<EndOfStreamException>(()=>reader.Read(7,default));
    }
    [Fact]
    public void MalformedHeadersAreRejectedBeforePayloadRead()
    {
        foreach(int corruption in Enumerable.Range(0,5))
        {
            byte[] data=JournalWire.Encode(new(JournalMessageKind.Output,1,1,new byte[]{1}));
            switch(corruption)
            {
                case 0: BinaryPrimitives.WriteInt32LittleEndian(data,int.MaxValue); break;
                case 1: data[4]=2; break;
                case 2: data[5]=255; break;
                case 3: data[6]=2; break;
                case 4: BinaryPrimitives.WriteInt32LittleEndian(data,18+1000000); break;
            }
            using var stream=new MemoryStream(data[..22]);
            var reader=new JournalReader(stream,memory=>stream.Read(memory.Span[..Math.Min(memory.Length,4)]));
            Assert.Throws<InvalidDataException>(()=>reader.Read(1,default));
        }
    }
    [Fact]
    public void PartialPayloadAndCancellationIncludingBufferedDataAreObserved()
    {
        byte[] data=JournalWire.Encode(new(JournalMessageKind.Output,1,1,new byte[]{1,2}));
        using var partial=new MemoryStream(data[..^1]);
        Assert.Throws<EndOfStreamException>(()=>new JournalReader(partial).Read(1,default));
        using var complete=new MemoryStream(data.Concat(data).ToArray());
        var reader=new JournalReader(complete);
        Assert.Equal(new byte[]{1,2},reader.Read(1,default).Payload.ToArray());
        using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(()=>reader.Read(1,cancelled.Token));
    }
}
