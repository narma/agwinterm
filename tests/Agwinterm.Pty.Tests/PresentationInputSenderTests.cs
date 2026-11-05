using Xunit;

namespace Agwinterm.Pty.Tests;

public class PresentationInputSenderTests
{
    [Fact]
    public async Task SlowWriterHasBoundedQueueAndDisposesAfterCancellationCompletes()
    {
        var stream=new BlockedWriter();
        using var sender=new PresentationInputSender(stream,"owner",1);
        var input=new TerminalInput.Raw(new byte[400*1024]);
        Assert.Equal(TerminalInputAcceptance.Accepted,sender.Submit(input));
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TerminalInputAcceptance.Accepted,sender.Submit(input));
        Assert.Equal(TerminalInputAcceptance.Accepted,sender.Submit(input));
        Assert.Equal(TerminalInputAcceptance.Overflow,sender.Submit(input));
        await sender.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stream.Disposed); Assert.False(stream.DisposedInFlight);
        Assert.Equal(TerminalInputAcceptance.Closed,sender.Submit(input));
    }

    [Fact]
    public async Task AcceptedInputKeepsOrderAndOwnsItsBytes()
    {
        var stream=new RecordingWriter(20);
        using var sender=new PresentationInputSender(stream,"owner",1);
        byte[] reused=new byte[1];
        for(int i=0;i<20;i++)
        {
            reused[0]=(byte)i;
            Assert.Equal(TerminalInputAcceptance.Accepted,sender.Submit(new TerminalInput.Raw(reused)));
        }
        await stream.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var records=new MemoryStream(stream.ToArray());
        for(ulong i=0;i<20;i++)
        {
            var decoded=await PresentationInputCodec.ReadAsync(records,"owner",1,i,default);
            Assert.Equal((byte)i,Assert.IsType<TerminalInput.Raw>(decoded.Input).Bytes.Span[0]);
        }
        sender.Dispose(); await sender.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class BlockedWriter : MemoryStream
    {
        public readonly TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _inFlight;
        public bool Disposed,DisposedInFlight;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellation=default)
        {
            _inFlight=true; Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite,cancellation); }
            finally { _inFlight=false; }
        }
        protected override void Dispose(bool disposing) { Disposed=true; DisposedInFlight|=_inFlight; base.Dispose(disposing); }
    }
    private sealed class RecordingWriter(int expected) : MemoryStream
    {
        public readonly TaskCompletionSource Finished=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;
        private int _parsed;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellation=default)
        {
            Write(buffer.Span);
            while(Length-_parsed >= 4)
            {
                int size=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(GetBuffer().AsSpan(_parsed,4));
                if(Length-_parsed < 4+size) break;
                _parsed+=4+size;
                if(++_count == expected) Finished.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }
    }
}
