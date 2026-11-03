using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class AgtermPresentationPipeTests
{
    [Theory]
    [InlineData("exit")]
    [InlineData("cancel")]
    [InlineData("stall")]
    public async Task JobChannelRetainsOutcomeAndBoundsAnUnreadContext(string mode)
    {
        var router = new Router();
        string name = "aw-job-test-" + Guid.NewGuid().ToString("N"), session = Guid.NewGuid().ToString();
        var scope = new AgtermOriginPresentationScope(WindowsIdentity.GetCurrent().User!.Value, router.Id, session,
            new("Ubuntu", "alice", "origin", "/usr/bin/zmx", "/tmp/zmx"));
        var job = new AgtermOverlayJob(scope, 1, 1, null,
            JsonSerializer.SerializeToElement(new { command = new string('x', mode == "stall" ? 100_000 : 1) }), false, 0);
        router.Host.JobOpen = (s, id, _, account) =>
        {
            Assert.Equal(job.Id, id); job.Claim(scope with { Session = s, Account = account }, 1, 1, 1); return job;
        };
        using var server = new ControlServer(router.Host, router, name);
        server.Start();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await client.ConnectAsync(2000);
        var request = JsonSerializer.Serialize(new { cmd = "zmx.export.job", window = router.Id, target = session, job = job.Id,
            args = new { distribution = "Ubuntu", user = "alice", origin = "origin", executable = "/usr/bin/zmx", socketDirectory = "/tmp/zmx", adapter = "/tmp/adapter" } });
        await client.WriteAsync(Encoding.UTF8.GetBytes(request + "\n"));
        if (mode == "stall")
        {
            Assert.Equal("channel-lost", await job.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
            return;
        }
        using var reader = new StreamReader(client, Encoding.UTF8, false, 4096, true);
        Assert.Contains("context", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        if (mode == "cancel")
        {
            job.Close();
            Assert.Contains("cancel", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        }
        await client.WriteAsync("{\"kind\":\"exit\",\"code\":17}\n"u8.ToArray());
        Assert.Null(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(mode == "cancel" ? "closed" : "exit 17", await job.Completion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BundledCliExitsWithOpenStdinAndClosedOriginOrStalledStdout(bool stalledOutput)
    {
        string name = "aw-relay-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "agwintermctl.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "zmx", "export", "present", "--window", Guid.NewGuid().ToString(), "--target", Guid.NewGuid().ToString(),
            "--distribution", "Ubuntu", "--user", "alice", "--origin", "origin", "--executable", "/usr/bin/zmx", "--socket-directory", "/tmp/zmx",
            "--adapter", "/tmp/adapter", "--pipe", name }) start.ArgumentList.Add(arg);
        using var child = System.Diagnostics.Process.Start(start)!;
        try
        {
            await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, true);
            var request = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Contains("zmx.export.present", request);
            const string hello = "{\"kind\":\"hello\",\"gen\":1,\"rev\":0,\"hello\":{\"version\":1,\"mode\":\"mirror\",\"kinds\":[]}}";
            await child.StandardInput.WriteLineAsync(hello);
            Assert.Equal(hello, await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
            if (stalledOutput)
            {
                // Exceed the anonymous stdout buffer without consuming any output. The relay's
                // own five-second deadline must end it, while both origin and stdin stay open.
                string large = JsonSerializer.Serialize(new { kind = "context", gen = 1, rev = 1, context = new string('x', 100_000) });
                await server.WriteAsync(Encoding.UTF8.GetBytes(large + "\n")).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(1, child.ExitCode);
                return;
            }
            await server.WriteAsync(Encoding.UTF8.GetBytes(hello + "\n"));
            Assert.Equal(hello, await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
            server.Disconnect();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(child.ExitCode == 0, await child.StandardError.ReadToEndAsync());
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
    }

    private sealed class Router : IWindowHost
    {
        public string Id = Guid.NewGuid().ToString();
        public FakeSessionHost Host = new();
        public ISessionHost? ResolveWindow(string? selector) => selector == Id ? Host : null;
        public IReadOnlyList<WindowSnapshot> Windows() => [new(Id, "fixture", true, false)];
        public string WindowNew(string? name) => throw new NotSupportedException();
        public bool WindowSelect(string? s) => false;
        public bool WindowClose(string? s) => false;
        public bool WindowDelete(string? s) => false;
        public bool WindowRename(string? s, string n) => false;
        public bool WindowResize(string? s, int w, int h) => false;
        public bool WindowMove(string? s, int x, int y) => false;
        public bool WindowZoom(string? s) => false;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactScopeStreamKeepsBufferedHelloAndRefusesCommandForwarding(bool wrongWindow)
    {
        var router = new Router();
        var broker = new AgtermPresentationBroker();
        string session = Guid.NewGuid().ToString(), name = "aw-presentation-test-" + Guid.NewGuid().ToString("N");
        var origin = new WslOriginScope("Ubuntu", "alice", "origin", "/usr/bin/zmx", "/tmp/zmx");
        var scope = new AgtermOriginPresentationScope(WindowsIdentity.GetCurrent().User!.Value, router.Id, session, origin);
        broker.Register(scope, JsonSerializer.SerializeToElement(new { }), ["context"]);
        int opens = 0;
        router.Host.PresentationOpen = (s, _, account, hello) => { opens++; return broker.Subscribe(scope with { Session = s, Account = account }, hello); };
        using var server = new ControlServer(router.Host, router, name);
        Exception? failure = null;
        server.PresentationStreamFailure = ex => failure = ex;
        server.Start();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await client.ConnectAsync(2000);
        using var reader = new StreamReader(client, Encoding.UTF8, false, 4096, true);
        var request = JsonSerializer.Serialize(new { cmd = "zmx.export.present", window = wrongWindow ? Guid.NewGuid().ToString() : router.Id,
            target = session, args = new { distribution = "Ubuntu", user = "alice", origin = "origin", executable = "/usr/bin/zmx", socketDirectory = "/tmp/zmx", adapter = "/tmp/adapter" } });
        var hello = "{\"kind\":\"hello\",\"gen\":0,\"rev\":0,\"hello\":{\"version\":1,\"kinds\":[\"context\"],\"mode\":\"mirror\"}}";
        await client.WriteAsync(Encoding.UTF8.GetBytes(request + "\n" + hello + "\n"));
        if (wrongWindow)
        {
            Assert.Null(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(0, opens);
            return;
        }
        var response = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(response is not null, failure?.ToString());
        Assert.Equal("hello", AgtermPresentationProtocol.Decode(Encoding.UTF8.GetBytes(response)).Kind);
        Assert.Contains("snapshot", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        await client.WriteAsync("{\"cmd\":\"session.type\",\"args\":{\"text\":\"untrusted\"}}\n"u8.ToArray());
        Assert.Null(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, opens);
        Assert.Equal(0, router.Host.LiveCalls);
    }
}
