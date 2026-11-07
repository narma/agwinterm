using System.IO.Pipes;
using Agwinterm.Core;
using Agwinterm.Pty.Proto;

namespace Agwinterm.Pty.Tests;

public class ProgramStatusReceiptTests
{
    [Fact]
    public async Task SlowOrTimedOutReceiptDoesNotBlockEffectsAndRetriesNewestToken()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts=0;
        var receipts = new ProgramStatusReceipts(async (_,token) => {
            if(Interlocked.Increment(ref attempts) == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(timeout.Token);
                throw new OperationCanceledException("Shared query gate timed out");
            }
            delivered.TrySetResult(token);
            return true;
        },timeout.Token,TimeSpan.FromMilliseconds(1));
        receipts.Enqueue("worker",1);
        await entered.Task.WaitAsync(timeout.Token);
        receipts.Enqueue("worker",2); // effect ingestion keeps running while the sender is stalled
        release.SetResult();
        Assert.Equal(2,await delivered.Task.WaitAsync(timeout.Token));
        timeout.Cancel();
    }

    [Fact]
    public async Task CompletionSurvivesFailedAttachAndOnlyMatchingReceiptConsumesIt()
    {
        using var session = new TerminalSession(80,24);
        using var frames = new GhosttyFrameSource((GhosttyTerminalCore)session.Emulator,session.SyncRoot,"owner",3,new());
        var records = new ProgramStatusRecords();
        records.Apply(new("done","worker","","","",null,null),retainCompletion:true);
        records.Apply(new("idle","worker","","","",null,null),retainCompletion:true);
        long token = records.Replay()[0].Completion;
        Assert.Equal(2,records.Replay().Count); // an attach that never receives the replay
        string name = "agw-receipt-"+Guid.NewGuid().ToString("N");
        var channel = new HostQueryChannel(name,session,"owner",3,frames,records.AcknowledgeCompletion);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serving = channel.RunAsync(timeout.Token);
        try
        {
            Assert.Equal("stale",await Receipt(2));
            Assert.Equal(2,records.Replay().Count);
            Assert.Equal("ok",await Receipt(3));
            Assert.Equal("idle",Assert.Single(records.Replay()).Report.State);
        }
        finally { timeout.Cancel(); await serving; }

        async Task<string> Receipt(ulong epoch)
        {
            using var pipe = new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            await PresentationQueryWire.WriteAsync(pipe,new PresentationQuery { Incarnation="owner",
                AttachmentEpoch=epoch,Operation="program-status-receipt",Generation=token,Text="worker" },timeout.Token);
            return (await PresentationQueryWire.ReadAsync(pipe,PresentationQueryReply.Parser,timeout.Token)).Status;
        }
    }
}
