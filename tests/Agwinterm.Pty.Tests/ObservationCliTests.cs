using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Agwinterm.Pty.Tests;

public class ObservationCliTests
{
    private static async Task<(int Exit, string Output, string Error, string[] Requests)> Run(string[] replies, params string[] arguments)
    {
        string name = "agwinterm-watch-test-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var serving = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync(deadline.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
            var requests = new List<string>();
            foreach (var reply in replies)
            {
                requests.Add((await reader.ReadLineAsync(deadline.Token))!);
                await pipe.WriteAsync(Encoding.UTF8.GetBytes(reply + "\n"), deadline.Token);
            }
            return requests.ToArray();
        });
        var result = CtlRequestTests.RunCtl([.. arguments, "--pipe", name]);
        return (result.ExitCode, result.StdOut, result.StdErr, await serving);
    }

    private static string Page(long oldest, long head, long cursor, bool gap, object[] events)
        => JsonSerializer.Serialize(new { ok = true, result = new { schema = 1, run = "R", window = "W", oldest, head, cursor, gap, more = cursor < head, events } });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WatchPinsIdentityFramesPagesAndExitsOnGap(bool json)
    {
        var result = await Run([
            Page(1, 0, 0, false, []),
            Page(1, 1, 1, false, [new { seq = 1, type = "status", window = "W", sessionId = "S", paneId = "P", info = "completed" }]),
            Page(10, 12, 12, true, [new { seq = 12, type = "tree", window = "W" }])
        ], json ? ["events", "--watch", "--json"] : ["events", "--watch"]);
        Assert.Equal(1, result.Exit);
        Assert.Contains("history gap", result.Error);
        using var initial = JsonDocument.Parse(result.Requests[0]);
        Assert.True(initial.RootElement.GetProperty("args").GetProperty("tail").GetBoolean());
        using var continuation = JsonDocument.Parse(result.Requests[1]);
        Assert.Equal("W", continuation.RootElement.GetProperty("window").GetString());
        Assert.Equal("R", continuation.RootElement.GetProperty("args").GetProperty("run").GetString());
        Assert.Equal(25000, continuation.RootElement.GetProperty("args").GetProperty("wait-ms").GetInt32());
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(json ? 3 : 1, lines.Length);
        if (json)
        {
            foreach (var line in lines) { using var parsed = JsonDocument.Parse(line); Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean()); }
        }
        else Assert.Equal("1\tstatus\tW\tS\tP\t\"completed\"", lines[0].TrimEnd('\r'));
    }

    [Theory]
    [InlineData("{\"ok\":true,\"result\":{\"cursor\":0,\"events\":[]}}")]
    [InlineData("{\"ok\":true,\"result\":{\"schema\":2}}")]
    [InlineData("{\"ok\":\"yes\"}")]
    [InlineData("not json")]
    public async Task UnsupportedAndMalformedWatchRepliesAreProtocolFailures(string reply)
    {
        var result = await Run([reply], "events", "--watch", "--json");
        Assert.Equal(1, result.Exit); Assert.Empty(result.Output);
    }

    [Fact]
    public async Task ExplicitResumeDoesNotAcceptPrefixWindowOrChangedRun()
    {
        var result = await Run([Page(1, 1, 1, false, [])], "events", "--watch", "--since", "1", "--run", "OLD", "--window", "W", "--json");
        Assert.Equal(1, result.Exit); Assert.Contains("identity changed", result.Error);
        var prefix = await Run([Page(1, 1, 1, false, [])], "events", "--watch", "--since", "1", "--run", "R", "--window", "prefix", "--json");
        Assert.Equal(1, prefix.Exit); Assert.Empty(prefix.Output);
    }

    [Theory]
    [InlineData("{\"ok\":true,\"result\":\"matched\"}")]
    [InlineData("{\"ok\":true,\"result\":{\"outcome\":\"matched\",\"run\":\"R\",\"window\":\"W\",\"scope\":\"pane\",\"paneId\":\"P\",\"elapsedMs\":0,\"status\":\"idle\",\"seq\":1,\"source\":\"transition\"}}")]
    public async Task WaitCannotClaimSuccessForMalformedOrNonmatchingReply(string reply)
    {
        var result = await Run([reply], "session", "wait", "completed", "--scope", "pane", "--target", "P", "--json");
        Assert.Equal(1, result.Exit); Assert.Empty(result.Output);
    }
}
