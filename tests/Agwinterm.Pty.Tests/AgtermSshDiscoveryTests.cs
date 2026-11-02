using System.Diagnostics;
using System.Text;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class AgtermSshDiscoveryTests
{
    [Theory]
    [InlineData("-oProxyCommand=evil")]
    [InlineData("host;echo")]
    [InlineData("user@$(command)")]
    [InlineData("host\nnext")]
    [InlineData("two hosts")]
    [InlineData("")]
    public void RejectsDestinationInjection(string host) => Assert.Throws<ArgumentException>(() =>
        AgtermSshDiscoveryClient.StartInfo(new("Ubuntu", "alice", host)));

    [Theory]
    [InlineData("origin")]
    [InlineData("alice@192.168.50.16")]
    [InlineData("alice@[::1]")]
    public void PinsSelectorsAndDoesNotEnrollKeysOrPrompt(string host)
    {
        var start = AgtermSshDiscoveryClient.StartInfo(new("Ubuntu", "alice", host, "/home/alice/a' b\\c.sock"));
        Assert.Equal("wsl.exe", start.FileName);
        Assert.Equal(new[] { "--distribution", "Ubuntu", "--user", "alice", "--exec", "/usr/bin/env",
            "SSH_AUTH_SOCK=/home/alice/a' b\\c.sock", "ssh", "-T" }, start.ArgumentList.Take(9));
        Assert.Contains("BatchMode=yes", start.ArgumentList);
        Assert.Contains("StrictHostKeyChecking=yes", start.ArgumentList);
        Assert.Contains("UpdateHostKeys=no", start.ArgumentList);
        Assert.Equal(host, start.ArgumentList[^2]);
        Assert.Contains("agtermctl zmx tree --json", start.ArgumentList[^1]);
    }

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("a'b", "'a'\\''b'")]
    [InlineData("a\\b", "'a'\\\\'b'")]
    [InlineData("a'\\b", "'a'\\'''\\\\'b'")]
    [InlineData("a!b", "'a'\\!'b'")]
    public void AccountShellQuotingKeepsMetacharactersLiteral(string input, string expected) =>
        Assert.Equal(expected, AgtermSshDiscoveryClient.QuoteAccountShell(input));

    // These are hidden, host-free child processes, not terminal UI fixtures or WSL sessions.
    static ProcessStartInfo Fixture(string script)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"));
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
        return start;
    }

    [Fact]
    public async Task ExitStatusWinsOverSuccessShapedOutput()
    {
        var result = await AgtermSshDiscoveryClient.RunAsync(Fixture("[Console]::Out.Write('{\"ok\":true}'); exit 7"),
            TimeSpan.FromSeconds(10), default);
        Assert.Equal(7, result.ExitCode);
        Assert.Throws<AgtermDiscoveryException>(() => AgtermRemoteDiscovery.Decode(result.ExitCode, result.Stdout));
    }

    [Fact]
    public async Task ReadsBothPipesAndRejectsTruncation()
    {
        var result = await AgtermSshDiscoveryClient.RunAsync(Fixture("[Console]::Error.Write(('e' * 50000)); [Console]::Out.Write('{\"ok\":'); exit 0"),
            TimeSpan.FromSeconds(10), default);
        Assert.Equal(0, result.ExitCode);
        Assert.Throws<AgtermDiscoveryException>(() => AgtermRemoteDiscovery.Decode(result.ExitCode, result.Stdout));
    }

    [Theory]
    [InlineData("[Console]::Out.Write(('x' * 4200000))")]
    [InlineData("[Console]::Error.Write(('x' * 100000))")]
    public async Task OversizedPipesFailWithoutDeadlock(string script) =>
        await Assert.ThrowsAsync<IOException>(() => AgtermSshDiscoveryClient.RunAsync(Fixture(script), TimeSpan.FromSeconds(10), default));

    [Fact]
    public async Task DeadlineAndCancellationStopLostReplies()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AgtermSshDiscoveryClient.RunAsync(
            Fixture("Start-Sleep -Seconds 60"), TimeSpan.FromMilliseconds(200), default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AgtermSshDiscoveryClient.RunAsync(
            Fixture("throw 'must not start'"), TimeSpan.FromSeconds(10), cancelled.Token));
    }
}
