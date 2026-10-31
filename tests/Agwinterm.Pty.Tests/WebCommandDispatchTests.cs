using System.Text.Json;
using Agwinterm.Core;

namespace Agwinterm.Pty.Tests;

public class WebCommandDispatchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DocumentInvalidationWhileQueuedPreventsTerminalMutation(bool invalidate)
    {
        var host = new FakeSessionHost();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        bool current = true;
        using var server = new ControlServer(host, sessionCommandExecutor: action =>
        {
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            if (!current) throw new InvalidOperationException("stale page request");
            return action();
        });
        var reply = Task.Run(() => server.Dispatch("""{"cmd":"session.status","args":{"status":"active"}}"""));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            current = !invalidate;
        }
        finally { resume.Set(); }
        using var json = JsonDocument.Parse(await reply);
        Assert.Equal(!invalidate, json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(invalidate ? AgentStatus.Idle : AgentStatus.Active, host.Resolve("active")!.Status);
    }
}
