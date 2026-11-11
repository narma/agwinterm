namespace Agwinterm.Pty.Tests;

public sealed class QueuedSessionStartTests
{
    [Fact]
    public async Task ClosingWhileQueuedDoesNotConnectOrSpawnAndReleasesTheSlot()
    {
        // There is intentionally no host: any attempt to start after close
        // would fail or wait for pipe discovery instead of completing quietly.
        using var backend = new ServerSessionBackend("queued-close-" + Guid.NewGuid().ToString("N"), null);
        await backend.LaunchGate.WaitAsync();
        using var first = new ServerSession(backend, "first", 80, 24);
        using var second = new ServerSession(backend, "second", 80, 24);
        int outputs = 0;
        first.OutputReceived += () => Interlocked.Increment(ref outputs);
        second.OutputReceived += () => Interlocked.Increment(ref outputs);
        var starts = new[] { first.StartAsync("must-not-run.exe", []), second.StartAsync("must-not-run.exe", []) };
        Assert.All(starts, task => Assert.False(task.IsCompleted));
        first.Detach(); second.Detach();
        backend.LaunchGate.Release();
        await Task.WhenAll(starts).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, outputs);
        Assert.Equal(1, backend.LaunchGate.CurrentCount);
    }
}
