using System.IO.Pipes;
using Xunit;

namespace Agwinterm.Pty.Tests;

public class PresentationPipeIoTests
{
    [Fact]
    public async Task UiSubmissionNeverWaitsForSlowReaderAndPendingPinSurvivesHandoff()
    {
        string name="agw-ui-pending-"+Guid.NewGuid().ToString("N");
        using var server=new NamedPipeServerStream(name,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096);
        using var client=new NamedPipeClientStream(".",name,PipeDirection.Out,PipeOptions.Asynchronous);
        var connected=server.WaitForConnectionAsync(); await client.ConnectAsync(); await connected;
        using var sender=new PresentationInputSender(client,"owner",1);
        var submitted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui=Task.Factory.StartNew(()=>
        {
            Assert.Equal(TerminalInputAcceptance.Accepted,sender.Submit(new TerminalInput.Raw(new byte[400*1024]),persistentUiThread:true));
            submitted.SetResult();
            sender.Completion.GetAwaiter().GetResult();
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sender.Dispose(); await ui.WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact]
    public async Task SlowReaderCancellationDrainsNativeWriteAndKeepsPipeDisposalWithWriter()
    {
        string name="agw-output-cancel-"+Guid.NewGuid().ToString("N");
        using var server=new NamedPipeServerStream(name,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096);
        using var client=new NamedPipeClientStream(".",name,PipeDirection.Out,PipeOptions.Asynchronous);
        var connected=server.WaitForConnectionAsync(); await client.ConnectAsync(); await connected;
        var handle=client.SafePipeHandle;
        using var sender=new PresentationInputSender(client,"owner",1);
        Assert.Equal(TerminalInputAcceptance.Accepted,sender.Submit(new TerminalInput.Raw(new byte[400*1024])));
        await Task.Delay(20); // Leave the reader stalled, outside any measured workload.
        sender.Dispose();
        await sender.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(handle.IsClosed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectWriterPreservesRecordOrderOverRealPipe(bool mixedQueuedAndImmediate)
    {
        string name="agw-output-order-"+Guid.NewGuid().ToString("N");
        using var server=new NamedPipeServerStream(name,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        using var client=new NamedPipeClientStream(".",name,PipeDirection.Out,PipeOptions.Asynchronous);
        var connected=server.WaitForConnectionAsync(); await client.ConnectAsync(); await connected;
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var sender=new PresentationInputSender(client,"owner",1);
        var reading=Task.Run(async ()=>
        {
            for(ulong i=0;i<1000;i++)
            {
                var result=await PresentationInputCodec.ReadAsync(server,"owner",1,i,cancellation.Token);
                Assert.Equal(new TerminalInput.Text(i.ToString()),result.Input);
            }
        });
        var sending=Task.Factory.StartNew(()=>
        {
            // Model the persistent UI thread. Windows can abort pending pipe IO
            // when its issuing thread exits, even if another thread waits on it.
            for(int i=0;i<1000;i++) Assert.Equal(TerminalInputAcceptance.Accepted,
                sender.Submit(new TerminalInput.Text(i.ToString()),persistentUiThread:!mixedQueuedAndImmediate || i % 2 != 0));
            reading.GetAwaiter().GetResult();
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        try { await sending; }
        catch { Assert.Null(sender.Failure); throw; }
        sender.Dispose(); await sender.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact]
    public async Task ReadsPartialRecordsAndEofWithoutManagedCompletionCallbacks()
    {
        string name="agw-input-test-"+Guid.NewGuid().ToString("N");
        using var server=new NamedPipeServerStream(name,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        using var client=new NamedPipeClientStream(".",name,PipeDirection.Out,PipeOptions.Asynchronous);
        var connected=server.WaitForConnectionAsync(); await client.ConnectAsync(); await connected;
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var reader=new PresentationPipeIo(server,cancellation.Token);
        byte[] actual=new byte[128];
        var reading=Task.Run(()=>
        {
            int offset=0;
            while(offset < actual.Length) offset+=reader.Read(actual.AsMemory(offset));
            Assert.Equal(0,reader.Read(new byte[1]));
        });
        for(int i=0;i<128;i++) await client.WriteAsync(new byte[]{(byte)i});
        client.Dispose();
        await reading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Enumerable.Range(0,128).Select(i=>(byte)i),actual);
    }

    [Fact]
    public async Task CancellationDrainsPendingReadBeforeItsStorageIsReleased()
    {
        string name="agw-input-cancel-"+Guid.NewGuid().ToString("N");
        using var server=new NamedPipeServerStream(name,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        using var client=new NamedPipeClientStream(".",name,PipeDirection.Out,PipeOptions.Asynchronous);
        var connected=server.WaitForConnectionAsync(); await client.ConnectAsync(); await connected;
        // Exercise both pre-submission cancellation and a blocked overlapped read.
        for(int i=0;i<20;i++)
        {
            using var cancellation=new CancellationTokenSource();
            using var reader=new PresentationPipeIo(server,cancellation.Token);
            var reading=Task.Run(()=>Assert.Throws<OperationCanceledException>(()=>reader.Read(new byte[16])));
            if(i % 2 == 0) await Task.Delay(10);
            cancellation.Cancel();
            await reading.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
