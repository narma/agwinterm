using System.Buffers.Binary;
using System.IO.Pipes;
using Xunit;

namespace Agwinterm.Pty.Tests;

public class JournalBrokerTests
{
    [Fact]
    public async Task SeparateUploadCannotTrimBeforeItsInputCutAndReplacementRevokesPendingUpload()
    {
        // Transport only: no ConPTY, native terminal, child or desktop.
        using var session=new TerminalSession(80,24,"journal-broker");
        using var broker=new JournalBroker(session,"incarnation");
        broker.Journal.AppendOutput([27,91]);
        var attachment=broker.Attach("journal-unit-"+Guid.NewGuid().ToString("N"));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var output=new NamedPipeClientStream(".",attachment.Output,PipeDirection.In,PipeOptions.Asynchronous);
        using var input=new NamedPipeClientStream(".",attachment.Input,PipeDirection.Out,PipeOptions.Asynchronous);
        await output.ConnectAsync(timeout.Token); await input.ConnectAsync(timeout.Token);
        Assert.Equal(JournalMessageKind.Checkpoint,(await JournalWire.ReadAsync(output,attachment.Epoch,timeout.Token)).Kind);
        Assert.Equal(JournalMessageKind.Live,(await JournalWire.ReadAsync(output,attachment.Epoch,timeout.Token)).Kind);
        Assert.Equal(1,(await JournalWire.ReadAsync(output,attachment.Epoch,timeout.Token)).Sequence);
        using var locked=new ManualResetEventSlim(); using var release=new ManualResetEventSlim();
        var blockedWriter=Task.Run(()=> { lock(session.SyncRoot) { locked.Set(); release.Wait(timeout.Token); } });
        Assert.True(locked.Wait(TimeSpan.FromSeconds(5)));
        try
        {
        using(var upload=new NamedPipeClientStream(".",attachment.Input+"-checkpoint",PipeDirection.InOut,PipeOptions.Asynchronous))
        {
            await upload.ConnectAsync(timeout.Token);
            byte[] cut=new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(cut,2);
            await upload.WriteAsync(cut,timeout.Token);
            await upload.WriteAsync(JournalWire.Encode(new(JournalMessageKind.Checkpoint,attachment.Epoch,1,new byte[]{7,8})),timeout.Token);
            byte[] ack=new byte[1]; var acknowledged=upload.ReadExactlyAsync(ack,timeout.Token).AsTask();
            await Task.Delay(100,timeout.Token);
            Assert.False(acknowledged.IsCompleted); Assert.Equal(0,broker.Journal.Checkpoint.Sequence);
            byte[] dimensions=new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(dimensions,81); BinaryPrimitives.WriteInt32LittleEndian(dimensions.AsSpan(4),24);
            await input.WriteAsync(JournalWire.Encode(new(JournalMessageKind.ResizeRequest,attachment.Epoch,1,dimensions)),timeout.Token);
            await input.WriteAsync(JournalWire.Encode(new(JournalMessageKind.ResizeRequest,attachment.Epoch,2,dimensions)),timeout.Token);
            await acknowledged;
            Assert.Equal(1,ack[0]); Assert.Equal(1,broker.Journal.Checkpoint.Sequence);
            Assert.Equal(new byte[]{7,8},broker.Journal.Checkpoint.Snapshot.ToArray());
            await upload.WriteAsync(new byte[]{1},timeout.Token);
        }
        }
        finally { release.Set(); await blockedWriter; }
        Assert.Equal(JournalMessageKind.Resize,(await JournalWire.ReadAsync(output,attachment.Epoch,timeout.Token)).Kind);
        Assert.Equal(JournalMessageKind.Resize,(await JournalWire.ReadAsync(output,attachment.Epoch,timeout.Token)).Kind);
        using(var upload=new NamedPipeClientStream(".",attachment.Input+"-checkpoint",PipeDirection.InOut,PipeOptions.Asynchronous))
        {
            await upload.ConnectAsync(timeout.Token);
            byte[] cut=new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(cut,3);
            await upload.WriteAsync(cut,timeout.Token);
            await upload.WriteAsync(JournalWire.Encode(new(JournalMessageKind.Checkpoint,attachment.Epoch,2,new byte[]{9})),timeout.Token);
            await Task.Delay(100,timeout.Token);
            broker.Attach("journal-replacement-"+Guid.NewGuid().ToString("N"));
            byte[] ack=new byte[1];
            await Assert.ThrowsAnyAsync<IOException>(async()=>await upload.ReadExactlyAsync(ack,timeout.Token));
            Assert.Equal(1,broker.Journal.Checkpoint.Sequence);
        }
    }
}
