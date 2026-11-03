using System.Diagnostics;
using System.Text;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class AgtermPresentationTransportTests
{
    private static ProcessStartInfo Fixture(string script)
    {
        var start = new ProcessStartInfo("powershell.exe");
        foreach (var value in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(value);
        return start;
    }
    private const string Hello = "[Console]::Out.WriteLine('{\"kind\":\"hello\",\"gen\":1,\"rev\":0,\"hello\":{\"version\":1,\"kinds\":[\"status\"],\"mode\":\"mirror\"}}');";
    private const string Snapshot = "[Console]::Out.WriteLine('{\"kind\":\"snapshot\",\"gen\":1,\"rev\":1,\"snapshot\":{}}');";

    [Fact]
    public void UsesIndependentNonPtySshAndExactQuotedSession()
    {
        var binding = AgtermRemoteBindingTests.Binding() with { PresentationVersion = 1, OriginSession = "a'; touch /tmp/no; '" };
        var args = AgtermPresentationTransport.StartInfo(binding).ArgumentList;
        Assert.Contains("-T", args);
        Assert.DoesNotContain("-tt", args);
        Assert.Contains("StrictHostKeyChecking=yes", args);
        Assert.DoesNotContain("attach", args);
        Assert.Throws<ArgumentException>(() => AgtermPresentationTransport.StartInfo(binding with { Target = binding.Target with { Destination = "-bad" } }));
        Assert.Throws<ArgumentException>(() => AgtermPresentationTransport.StartInfo(binding with { PresentationVersion = null }));
    }

    [Theory]
    [InlineData("Start-Sleep -Seconds 60")]
    [InlineData("[Console]::Error.Write(('e' * 100000)); Start-Sleep -Seconds 60")]
    public async Task HandshakeAndStderrFailureAreBounded(string script)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await Assert.ThrowsAsync<IOException>(() => new AgtermPresentationTransport().RunAsync(Fixture(script), [],
            (_, _) => Task.CompletedTask, deadline.Token, handshake: TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task SnapshotIsDeliveredAndSilenceDoesNotHaveAnInfiniteWait()
    {
        var received = new List<string>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await Assert.ThrowsAsync<IOException>(() => new AgtermPresentationTransport().RunAsync(Fixture(Hello + Snapshot + "Start-Sleep -Seconds 60"), ["status"],
            (frame, _) => { received.Add(frame.Kind); return Task.CompletedTask; }, deadline.Token,
            handshake: TimeSpan.FromSeconds(2), silence: TimeSpan.FromMilliseconds(300)));
        Assert.Equal(new[] { "snapshot" }, received);
    }

    [Fact]
    public async Task StalledConsumerCannotGrowQueueWithoutBound()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var script = Hello + Snapshot + "2..600 | ForEach-Object { [Console]::Out.WriteLine(('{\"kind\":\"status\",\"gen\":1,\"rev\":REV}' -replace 'REV', $_)) }; Start-Sleep -Seconds 60";
        await Assert.ThrowsAsync<IOException>(() => new AgtermPresentationTransport().RunAsync(Fixture(script), ["status"],
            (_, token) => Task.Delay(Timeout.Infinite, token), deadline.Token));
    }

    [Fact]
    public async Task CancellationEndsOnlyOwnedProcess()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AgtermPresentationTransport().RunAsync(
            Fixture("Start-Sleep -Seconds 60"), [], (_, _) => Task.CompletedTask, deadline.Token));
    }

    [Fact]
    public async Task StalledWriterIsBoundedAndSeparateStreamSurvives()
    {
        var otherStart = Fixture("while ($true) { [Console]::Out.WriteLine('alive'); Start-Sleep -Milliseconds 100 }");
        otherStart.UseShellExecute = false;
        otherStart.CreateNoWindow = true;
        otherStart.RedirectStandardOutput = true;
        using var other = Process.Start(otherStart)!;
        try
        {
            Assert.Equal("alive", await other.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var capabilities = Enumerable.Range(0, 128).Select(n => n.ToString() + new string('x', 120)).ToArray();
            await Assert.ThrowsAnyAsync<Exception>(() => new AgtermPresentationTransport().RunAsync(
                Fixture("Start-Sleep -Seconds 60"), capabilities, (_, _) => Task.CompletedTask, deadline.Token,
                handshake: TimeSpan.FromMilliseconds(400)));
            Assert.False(other.HasExited);
            Assert.Equal("alive", await other.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { if (!other.HasExited) other.Kill(entireProcessTree: true); await other.WaitForExitAsync(); }
    }
}
