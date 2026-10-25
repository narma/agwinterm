using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class StatusSignalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothBackendsPublishImmutableTransitionsAndRetainLegacyNotifications(bool hosted)
    {
        using var backend = new ServerSessionBackend("test-status-" + Guid.NewGuid().ToString("N"), null);
        using ISession session = hosted ? new ServerSession(backend, "P", 80, 24) : new TerminalSession(80, 24);
        var values = new List<(StatusSnapshot Value, bool Changed)>();
        using var subscription = session.StatusSignal!.Subscribe((value, changed) => values.Add((value, changed)));
        int legacy = 0;
        session.StatusChanged += () => legacy++;
        session.SetStatus(AgentStatus.Completed, blink: true, autoReset: true);
        session.NotifyActivity();
        session.SetStatus(AgentStatus.Idle); // reassertion updates timestamp/version, not the legacy event
        Assert.Equal(2, legacy);
        Assert.Equal(new[] { AgentStatus.Idle, AgentStatus.Completed, AgentStatus.Idle, AgentStatus.Idle }, values.Select(v => v.Value.Status));
        Assert.True(values[1].Value.Blink);
        Assert.True(values[1].Value.AutoReset);
        Assert.False(values[2].Value.Blink);
        Assert.False(values[3].Changed);
        Assert.Equal(new long[] { 0, 1, 2, 3 }, values.Select(v => v.Value.Version));
    }

    [Fact]
    public async Task SubscriptionAndConcurrentWritersAreTotallyOrdered()
    {
        var signal = new StatusSignal();
        var values = new List<StatusSnapshot>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var subscribing = Task.Run(() => signal.Subscribe((value, _) =>
        {
            values.Add(value);
            if (value.Version == 0) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var writes = Task.Run(() => Parallel.For(0, 100, i => signal.Set(i % 2 == 0 ? AgentStatus.Completed : AgentStatus.Idle, false, false)));
        release.Set();
        using var subscription = await subscribing;
        await writes;
        Assert.Equal(Enumerable.Range(0, 101).Select(i => (long)i), values.Select(v => v.Version));
        Assert.Equal(50, values.Count(v => v.Status == AgentStatus.Completed));
        subscription.Dispose();
        signal.Set(AgentStatus.Blocked, false, false);
        Assert.Equal(101, values.Count);
    }
}
