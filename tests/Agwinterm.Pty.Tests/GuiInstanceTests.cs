namespace Agwinterm.Pty.Tests;

// Mutex ownership must stay on the constructing thread, including disposal.
#pragma warning disable xUnit1031
public sealed class GuiInstanceTests
{
    [Fact]
    public void DuplicateLaunchForwardsWithoutOwningLibraryAndOwnerCanRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gui-instance-" + Guid.NewGuid().ToString("N"));
        using (var owner = new GuiInstance(directory))
        {
            Assert.True(owner.IsOwner);
            var received = new TaskCompletionSource<GuiInstance.Launch>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.Listen(request => received.TrySetResult(request));
            var request = new GuiInstance.Launch("D:\\work", "pwsh", true, true, true);
            // Named mutex ownership is per thread; exercise a real competing thread.
            Task.Factory.StartNew(() =>
            {
                using var duplicate = new GuiInstance(directory.ToUpperInvariant() + Path.DirectorySeparatorChar);
                Assert.False(duplicate.IsOwner);
                duplicate.ForwardAsync(request).GetAwaiter().GetResult();
                using var separate = new GuiInstance(directory + "-other");
                Assert.True(separate.IsOwner);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();
            Assert.Equal(request, received.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        }
        using var restarted = new GuiInstance(directory);
        Assert.True(restarted.IsOwner);
    }

    [Fact]
    public void DuplicateWaitsForOwnerStartupWithoutTakingOver()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gui-instance-" + Guid.NewGuid().ToString("N"));
        using var owner = new GuiInstance(directory);
        using var connecting = new ManualResetEventSlim();
        var duplicate = Task.Factory.StartNew(() =>
        {
            using var instance = new GuiInstance(directory);
            Assert.False(instance.IsOwner);
            connecting.Set();
            instance.ForwardAsync(new()).GetAwaiter().GetResult();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(connecting.Wait(TimeSpan.FromSeconds(5)));
        owner.Listen(_ => { });
        duplicate.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }
}
