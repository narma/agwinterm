using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Ctl;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class ObservationTransportTests
{
    private sealed class Fixture : IDisposable
    {
        public string Pipe { get; } = "agwinterm-observe-" + Guid.NewGuid().ToString("N");
        public ControlObservation Journal { get; } = new("W");
        public StatusSignal Signal { get; } = new();
        public ControlServer Server { get; }
        public Fixture()
        {
            Journal.ObservePane("P", Signal); Journal.SetMembership("S", ["P"]);
            Server = new(new FakeSessionHost { Observation = Journal }, Pipe);
            Server.Start();
        }
        private bool _serverStopped;
        public void StopServer() { if (_serverStopped) return; _serverStopped = true; Server.Dispose(); }
        public void Dispose() { Journal.Close(); StopServer(); }
        public async Task<NamedPipeClientStream> Connect()
        {
            var pipe = new NamedPipeClientStream(".", Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(2000); return pipe;
        }
    }

    private static async Task AwaitPending(ControlObservation journal, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (journal.PendingCount != count) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task DisconnectAndPipelineMisuseReleasePendingObserver()
    {
        using var fixture = new Fixture();
        using (var pipe = await fixture.Connect())
        {
            await pipe.WriteAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"events\",\"args\":{\"wait-ms\":25000}}\n"));
            await AwaitPending(fixture.Journal, 1);
        }
        await AwaitPending(fixture.Journal, 0);
        using var pipelined = await fixture.Connect();
        using var reader = new StreamReader(pipelined, Encoding.UTF8, false, 1024, true);
        await pipelined.WriteAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"events\",\"args\":{\"wait-ms\":25000}}\n"));
        await AwaitPending(fixture.Journal, 1);
        await pipelined.WriteAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"ping\"}\n"));
        using var reply = JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)))!);
        Assert.Equal("protocol-error", reply.RootElement.GetProperty("code").GetString());
        await AwaitPending(fixture.Journal, 0);
    }

    [Fact]
    public async Task SequentialRequestsAndLongPollWakeShareOneReader()
    {
        using var fixture = new Fixture();
        using var pipe = await fixture.Connect();
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
        foreach (string command in new[] { "{\"cmd\":\"ping\"}", "{\"cmd\":\"events\",\"args\":{\"wait-ms\":25000}}", "{\"cmd\":\"ping\"}" })
        {
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(command + "\n"));
            if (command.Contains("events")) { await AwaitPending(fixture.Journal, 1); fixture.Journal.Publish("tree"); }
            using var reply = JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)))!);
            Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        }
    }

    [Fact]
    public async Task RealCliWaitHasDistinctMatchTimeoutAndTransportCodes()
    {
        using var fixture = new Fixture();
        fixture.Signal.Set(AgentStatus.Completed, false, false);
        var matched = CtlRequestTests.RunCtl("session", "wait", "COMPLETED", "--scope", "pane", "--target", "P", "--pipe", fixture.Pipe, "--json");
        Assert.Equal(0, matched.ExitCode);
        Assert.Contains("\"source\":\"snapshot\"", matched.StdOut);
        var caller = CtlRequestTests.RunCtlWithCaller("P", "session", "wait", "completed", "--scope", "pane", "--pipe", fixture.Pipe, "--json");
        Assert.Equal(0, caller.ExitCode);
        Assert.Contains("\"paneId\":\"P\"", caller.StdOut);
        fixture.Signal.Set(AgentStatus.Idle, false, false);
        var timeout = CtlRequestTests.RunCtl("session", "wait", "completed", "--target", "S", "--wait-timeout", "1", "--pipe", fixture.Pipe, "--json");
        Assert.Equal(3, timeout.ExitCode);
        Assert.Contains("\"outcome\":\"timeout\"", timeout.StdOut);
        var transport = CtlRequestTests.RunCtl("session", "wait", "completed", "--target", "S", "--wait-timeout", "0", "--timeout", "1", "--pipe", fixture.Pipe);
        Assert.Equal(1, transport.ExitCode);
        Assert.Contains("transport deadline", transport.StdErr);
        await AwaitPending(fixture.Journal, 0);
        Assert.Equal(2, CtlRequestTests.RunCtl("session", "wait", "completed,completed", "--pipe", fixture.Pipe).ExitCode);
        Assert.Equal(2, CtlRequestTests.RunCtl("events", "--watch", "--since", "1", "--pipe", fixture.Pipe).ExitCode);
        var wide = CtlRequestTests.RunCtl("events", "--since", "4294967296", "--pipe", fixture.Pipe, "--json");
        Assert.Equal(1, wide.ExitCode);
        Assert.Contains("cursor-ahead", wide.StdOut); // Int64 reached server rather than becoming zero
    }

    [Fact]
    public async Task ClientCancellationProducesOneClientSourcedWaitOutcome()
    {
        using var fixture = new Fixture();
        using var cancel = new CancellationTokenSource();
        var args = new Dictionary<string, object?> { ["scope"] = "pane", ["statuses"] = new[] { "completed" }, ["timeout-ms"] = 0 };
        using var output = new StringWriter(); using var errors = new StringWriter();
        var task = ObservationCli.RunAsync(fixture.Pipe, new() { ["cmd"] = "session.wait", ["target"] = "P" }, args,
            new Dictionary<string, string>(), true, output, errors, cancel.Token);
        await AwaitPending(fixture.Journal, 1); cancel.Cancel();
        Assert.Equal(130, await task);
        using var reply = JsonDocument.Parse(output.ToString());
        Assert.Equal("client", reply.RootElement.GetProperty("source").GetString());
        Assert.Equal("cancelled", reply.RootElement.GetProperty("code").GetString());
        await AwaitPending(fixture.Journal, 0);
    }

    [Fact]
    public async Task SlowReplyDoesNotBlockOtherClientsOrShutdownCleanup()
    {
        using var fixture = new Fixture();
        for (int i = 0; i < 1000; i++) fixture.Journal.Publish("notification", new Dictionary<string, object?> { ["info"] = new string('x', 6000) });
        using (var slow = await fixture.Connect())
        {
            await slow.WriteAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"events\"}\n"));
            // Deliberately do not consume this multi-megabyte response.
            Assert.Equal(0, CtlRequestTests.RunCtl("ping", "--pipe", fixture.Pipe).ExitCode);
            fixture.Signal.Set(AgentStatus.Completed, false, false);
            Assert.Equal(AgentStatus.Completed, fixture.Journal.Status("pane", "P")!.Value.Value.Status);
        }
        using var pending = await fixture.Connect();
        await pending.WriteAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"session.wait\",\"target\":\"P\",\"args\":{\"scope\":\"pane\",\"statuses\":[\"blocked\"],\"timeout-ms\":0}}\n"));
        await AwaitPending(fixture.Journal, 1);
        fixture.StopServer();
        await AwaitPending(fixture.Journal, 0);
    }
}
