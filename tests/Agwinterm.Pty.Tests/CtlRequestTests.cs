using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Agwinterm.Ctl;

namespace Agwinterm.Pty.Tests;

/// <summary>
/// The CLI's reply deadline (#315). A server that accepts the request and never answers used to leave
/// agwintermctl blocked forever; these fake servers stand in for that app, one that answers, and one
/// that hangs up.
/// </summary>
public class CtlRequestTests : IDisposable
{
    private readonly List<IDisposable> _open = [];

    public void Dispose()
    {
        for (int i = _open.Count - 1; i >= 0; i--)
        {
            try { _open[i].Dispose(); } catch (IOException) { /* a closed pipe end is not a failure */ }
        }
        _open.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>A one-connection server: reads the request line, then answers <paramref name="reply"/>,
    /// hangs up when it is null and <paramref name="hangUp"/>, or else says nothing and holds the pipe.</summary>
    private string StartServer(string? reply, bool hangUp = false)
    {
        string pipeName = "agwinterm-test-ctl-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _open.Add(server);
        _ = Task.Run(async () =>
        {
            try
            {
                await server.WaitForConnectionAsync();
                var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
                await reader.ReadLineAsync();
                if (reply is not null)
                {
                    var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                    await writer.WriteLineAsync(reply);
                }
                else if (hangUp) server.Disconnect();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* test over */ }
        });
        return pipeName;
    }

    [Theory]
    [InlineData(null, false, 30)]
    [InlineData(null, true, -1)]   // a blocking overlay open waits for its overlay by default
    [InlineData("0", false, -1)]
    [InlineData("5", false, 5)]
    [InlineData("5", true, 5)]     // an explicit --timeout bounds even a waiting verb
    public void TimeoutResolvesFromOptionAndVerb(string? option, bool waits, int expectedSeconds)
    {
        Assert.True(CtlRequest.TryResolveTimeout(option, waits, out var timeout, out var error));
        Assert.Null(error);
        Assert.Equal(expectedSeconds < 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(expectedSeconds), timeout);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("")]
    public void TimeoutThatIsNotWholeSecondsIsRefused(string option)
    {
        Assert.False(CtlRequest.TryResolveTimeout(option, false, out _, out var error));
        Assert.Contains("--timeout needs a whole number of seconds", error);
        Assert.Contains("Nothing sent.", error);
    }

    [Fact]
    public void ExchangeReturnsTheReplyLine()
    {
        string pipe = StartServer("{\"ok\":true,\"result\":\"pong\"}");
        Assert.Equal("{\"ok\":true,\"result\":\"pong\"}", CtlRequest.Exchange(pipe, "{\"cmd\":\"ping\"}", TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void ExchangeReturnsNullWhenTheAppHangsUpWithoutAReply()
    {
        string pipe = StartServer(null, hangUp: true);
        Assert.Null(CtlRequest.Exchange(pipe, "{\"cmd\":\"ping\"}", TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void ExchangeGivesUpWhenTheAppAcceptsButNeverReplies()
    {
        string pipe = StartServer(null);
        var clock = Stopwatch.StartNew();
        var ex = Assert.Throws<ReplyTimeoutException>(() => CtlRequest.Exchange(pipe, "{\"cmd\":\"ping\"}", TimeSpan.FromSeconds(1)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"gave up after {clock.Elapsed}, not near the 1 s deadline");
        Assert.Equal(TimeSpan.FromSeconds(1), ex.Timeout);
        Assert.Contains("may still have been carried out", ex.Message);
    }

    [Fact]
    public void ExchangeWithNoPipeStillReportsAConnectTimeout()
    {
        Assert.Throws<TimeoutException>(() =>
            CtlRequest.Exchange("agwinterm-test-ctl-absent-" + Guid.NewGuid().ToString("N"), "{\"cmd\":\"ping\"}", TimeSpan.FromSeconds(10)));
    }

    // ---- the CLI end to end: the case #315 reported was the process that never exits ----------------

    [Fact]
    public void CtlExitsWithAnErrorWhenTheAppNeverReplies()
    {
        string pipe = StartServer(null);
        var (code, _, stderr) = RunCtl("ping", "--pipe", pipe, "--timeout", "1");
        Assert.Equal(1, code);
        Assert.Contains("no reply from agwinterm within 1 s", stderr);
    }

    [Fact]
    public void CtlRefusesAMalformedTimeoutWithoutConnecting()
    {
        var (code, _, stderr) = RunCtl("ping", "--pipe", "agwinterm-test-ctl-unused", "--timeout", "soon");
        Assert.Equal(2, code);
        Assert.Contains("--timeout needs a whole number of seconds", stderr);
    }

    [Fact]
    public void CtlStillPrintsAPromptReply()
    {
        string pipe = StartServer("{\"ok\":true,\"result\":\"pong\"}");
        var (code, stdout, _) = RunCtl("ping", "--pipe", pipe);
        Assert.Equal(0, code);
        Assert.Equal("pong", stdout.Trim());
    }

    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 1)]
    [InlineData(false, false, 1)]
    public void ReplyOutcomeDoesNotDependOnOutputFormat(bool ok, bool json, int expected)
    {
        string reply = ok ? "{\"ok\":true,\"result\":\"Привет ─ 世界\"}" : "{\"ok\":false,\"error\":\"session not found\"}";
        string pipe = StartServer(reply);
        var args = new List<string> { "ping", "--pipe", pipe };
        if (json) args.Add("--json");
        var (code, stdout, stderr) = RunCtl(args.ToArray());
        Assert.Equal(expected, code);
        Assert.Equal(json ? reply : ok ? "Привет ─ 世界" : "", stdout.Trim());
        Assert.Equal(!ok && !json ? "session not found" : "", stderr.Trim());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"ok\":\"true\"}")]
    public void InvalidJsonReplyIsAProtocolFailure(string reply)
    {
        var (code, stdout, stderr) = RunCtl("ping", "--json", "--pipe", StartServer(reply));
        Assert.Equal(1, code);
        Assert.Empty(stdout);
        Assert.Contains("invalid control reply", stderr);
    }

    [Fact]
    public void OfflineVersionAndHelpRemainLocalDiagnostics()
    {
        string pipe = "agwinterm-test-absent-" + Guid.NewGuid().ToString("N");
        Assert.Equal(0, RunCtl("version", "--pipe", pipe).ExitCode);
        Assert.Equal(0, RunCtl("session", "close", "--help", "--pipe", pipe).ExitCode);
        Assert.Equal(1, RunCtl("ping", "--json", "--pipe", pipe).ExitCode);
    }

    [Theory]
    [InlineData("session", "focus", null)] [InlineData("session", "resize", null)] [InlineData("command", "run", null)]
    [InlineData("session", "focus", "active")] [InlineData("session", "resize", "pane-B")] [InlineData("command", "run", "pane-B")]
    public async Task TargetedVerbsDoNotInheritCaller(string area, string verb, string? target)
    {
        string pipe = "agwinterm-test-route-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var exchange = Task.Run(async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await server.WaitForConnectionAsync(deadline.Token);
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
            string request = (await reader.ReadLineAsync(deadline.Token))!;
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            await writer.WriteLineAsync("{\"ok\":true,\"result\":\"accepted\"}");
            return request;
        });
        var argv = new List<string> { area, verb, "--pipe", pipe };
        if (area == "command") argv.AddRange(["echo literal", "--mode", "send"]);
        if (target is not null) argv.AddRange(["--target", target]);
        Assert.Equal(0, RunCtlWithCaller("pane-A", [.. argv]).ExitCode);
        using var doc = System.Text.Json.JsonDocument.Parse(await exchange);
        if (target is null) Assert.False(doc.RootElement.TryGetProperty("target", out _));
        else Assert.Equal(target, doc.RootElement.GetProperty("target").GetString());
    }

    internal static (int ExitCode, string StdOut, string StdErr) RunCtl(params string[] args) => RunCtlWithCaller(null, args);

    private static (int ExitCode, string StdOut, string StdErr) RunCtlWithCaller(string? caller, params string[] args)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "agwintermctl.exe");
        Assert.True(File.Exists(exe), $"agwintermctl apphost was not copied to {AppContext.BaseDirectory}");

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        if (caller is not null) start.Environment["AGWINTERM_SESSION_ID"] = caller;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start agwintermctl");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit(10_000);
        if (!exited) process.Kill(entireProcessTree: true);
        Assert.True(exited, "agwintermctl did not exit within 10 seconds");
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}
