using Xunit;

namespace Agwinterm.Pty.Tests;

public class TerminalJournalTests
{
    [Fact]
    public async Task OutputAndResizeAreOwnedAndOrderedAndReadsDoNotTrim()
    {
        using var journal=new TerminalJournal("session",1024);
        byte[] input=[1,2,3];
        Assert.Equal(1,journal.AppendOutput(input)); input[0]=99;
        Assert.Equal(2,journal.AppendResize(80,24));
        var output=await journal.ReadNextAsync(0,default);
        Assert.Equal(new byte[]{1,2,3},output!.Payload.ToArray());
        var resize=await journal.ReadNextAsync(1,default);
        Assert.Equal(JournalEventKind.Resize,resize!.Kind);
        Assert.Equal(80,System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(resize.Payload.Span));
        Assert.Equal(75,journal.RetainedBytes);
    }

    [Fact]
    public async Task SuccessfulPersistenceAloneReleasesBlockedProducer()
    {
        using var journal=new TerminalJournal("session",33);
        journal.AppendOutput([1]);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var producer=Task.Run(()=>journal.AppendOutput([2],timeout.Token));
        var saving=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpoint=journal.PublishCheckpointAsync("session",1,new byte[]{7},async (_,ct)=>
        { saving.SetResult(); await release.Task.WaitAsync(ct); },timeout.Token);
        await saving.Task.WaitAsync(timeout.Token);
        Assert.False(producer.IsCompleted);
        Assert.Equal(33,journal.RetainedBytes);
        release.SetResult(); await checkpoint;
        Assert.Equal(2,await producer);
        Assert.Equal(1,journal.Checkpoint.Sequence);
        Assert.Equal(33,journal.RetainedBytes);
        Assert.Equal(new byte[]{2},(await journal.ReadNextAsync(1,timeout.Token))!.Payload.ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await journal.ReadNextAsync(0,timeout.Token));
    }

    [Fact]
    public async Task FailedOrStaleCheckpointPreservesRecoverablePrefix()
    {
        using var journal=new TerminalJournal("session");
        journal.AppendOutput([1]);
        await Assert.ThrowsAsync<IOException>(()=>journal.PublishCheckpointAsync("session",1,new byte[]{7},(_,_)=>throw new IOException("disk")));
        Assert.Equal(0,journal.Checkpoint.Sequence); Assert.Equal(33,journal.RetainedBytes);
        foreach(var (identity,sequence) in new[]{("other",1L),("session",2L),("session",-1L)})
            await Assert.ThrowsAsync<InvalidDataException>(()=>journal.PublishCheckpointAsync(identity,sequence,new byte[]{7},(_,_)=>Task.CompletedTask));
        await journal.PublishCheckpointAsync("session",1,new byte[]{7},(_,_)=>Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidDataException>(()=>journal.PublishCheckpointAsync("session",0,new byte[]{8},(_,_)=>Task.CompletedTask));
        Assert.Equal(new byte[]{7},journal.Checkpoint.Snapshot.ToArray());
    }

    [Fact]
    public async Task CancellationWakesFullProducerAndIdleReader()
    {
        using var journal=new TerminalJournal("session",33);
        journal.AppendOutput([1]);
        using var cancellation=new CancellationTokenSource();
        var producer=Task.Run(()=>journal.AppendOutput([2],cancellation.Token));
        var reader=journal.ReadNextAsync(1,cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await producer.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await reader);
        Assert.Equal(1,journal.LastSequence);
    }

    [Fact]
    public async Task CheckpointOwnsBytesAndRetainsOnlyUncoveredTail()
    {
        using var journal=new TerminalJournal("session");
        journal.AppendOutput([1]); journal.AppendResize(90,30); journal.AppendOutput([3]);
        byte[] snapshot=[7];
        await journal.PublishCheckpointAsync("session",2,snapshot,(_,_)=>Task.CompletedTask); snapshot[0]=99;
        Assert.Equal(33,journal.RetainedBytes); Assert.Equal(7,journal.Checkpoint.Snapshot.Span[0]);
        journal.Complete();
        Assert.Equal(3,(await journal.ReadNextAsync(2,default))!.Sequence);
        Assert.Null(await journal.ReadNextAsync(3,default));
        Assert.Throws<InvalidOperationException>(()=>journal.AppendOutput([4]));
    }

    [Fact]
    public async Task CloseWakesWaitingOperationsAndLatePersistenceCannotPublish()
    {
        var journal=new TerminalJournal("session",33); journal.AppendOutput([1]);
        var producer=Task.Run(()=>journal.AppendOutput([2]));
        var reader=journal.ReadNextAsync(1,default).AsTask();
        var saving=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpoint=journal.PublishCheckpointAsync("session",1,new byte[]{7},async (_,_)=>{saving.SetResult();await release.Task;});
        await saving.Task; journal.Dispose(); release.SetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(async()=>await checkpoint);
        await Assert.ThrowsAsync<ObjectDisposedException>(async()=>await producer.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<ObjectDisposedException>(async()=>await reader);
    }
}
