using System.IO.Pipes;
using Agwinterm.Pty.Proto;
using Xunit;

namespace Agwinterm.Pty.Tests;

public class HostEffectChannelTests
{
    [Fact]
    public async Task EffectsRemainOrderedAndEndAfterReliableQueueDrains()
    {
        string name="agw-effects-test-"+Guid.NewGuid().ToString("N");
        var channel=new HostEffectChannel(name,"owner",3,()=>throw new InvalidOperationException("overflow"));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var writer=channel.RunAsync(timeout.Token);
        using var reader=new NamedPipeClientStream(".",name,PipeDirection.In,PipeOptions.Asynchronous);
        await reader.ConnectAsync(timeout.Token);
        var effect=new PresentationEffect { Kind=3,Text="clipboard" };
        channel.Publish(effect); effect.Text="changed by caller";
        channel.Publish(new() { Kind=4 }); channel.Complete();
        channel.Publish(new() { Kind=1,Text="late" });
        var first=await PresentationQueryWire.ReadAsync(reader,PresentationEffect.Parser,timeout.Token);
        var second=await PresentationQueryWire.ReadAsync(reader,PresentationEffect.Parser,timeout.Token);
        var end=await PresentationQueryWire.ReadAsync(reader,PresentationEffect.Parser,timeout.Token);
        await writer;
        Assert.Equal("clipboard",first.Text); Assert.Equal(1ul,first.Sequence);
        Assert.Equal("owner",first.Incarnation); Assert.Equal(3ul,first.AttachmentEpoch);
        Assert.Equal(4u,second.Kind); Assert.Equal(2ul,second.Sequence);
        Assert.Equal(0u,end.Kind); Assert.Equal(3ul,end.Sequence);
    }

    [Fact]
    public async Task OverflowRevokesAttachmentInsteadOfDroppingAcceptedEffects()
    {
        var overflow=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel=new HostEffectChannel("unused","owner",1,()=>overflow.TrySetResult());
        for(int i=0;i<257;i++) channel.Publish(new() { Kind=4 });
        await overflow.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
