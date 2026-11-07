using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

/// <summary>Pty-host integration tests (#105, Phase 2a): a real PtyHostServer over real named pipes
/// hosting real child processes — only the process boundary of `--pty-host` is elided.</summary>
[CollectionDefinition("Hosted process lifecycle", DisableParallelization = true)]
public class HostedProcessLifecycleCollection { }

[Collection("Hosted process lifecycle")]
public class PtyHostTests : IDisposable
{
    private readonly string _appId = "agwinterm-test-" + Guid.NewGuid().ToString("N")[..8];
    private readonly PtyHostServer _server;

    public PtyHostTests() => _server = new PtyHostServer(_appId, null, ghosttyOnly: false);
    public void Dispose() => _server.Dispose();

    [Fact]
    public void CreationTickets_ReconcileLostRepliesAndGuardReusedIds()
        => CreationProtocolAssertions.LostRepliesAndReusedId(_appId);

    [Fact]
    public Task CreationTickets_StartupSweepProtectsUnpublishedPane()
        => CreationProtocolAssertions.StartupSweepKeepsPendingPane(_appId);

    [Fact]
    public async Task CreationTickets_CancelBetweenSpawnAndPublicationCleansExactChild()
    {
        using var reached = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        System.Diagnostics.Process? child = null;
        _server.BeforeCreationPublication = pid =>
        {
            child = System.Diagnostics.Process.GetProcessById(pid!.Value);
            _ = child.SafeHandle; // pin the original process before allowing cleanup
            reached.Set();
            if (!release.Wait(15000)) throw new TimeoutException("publication barrier not released");
        };
        using var client = PtyHostClient.Connect(_appId);
        string ticket = client.PrepareCreate("barrier");
        var create = Task.Run(() => Record.Exception(() => client.Create("barrier", 80, 24, "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 120"], freshEnv: false, creationTicket: ticket)));
        try
        {
            Assert.True(reached.Wait(10000), "real child must exist before cancellation");
            using var cancel = PtyHostClient.Connect(_appId);
            Assert.Equal(Proto.CreationPhase.CreationCreating, cancel.QueryCreate("barrier", ticket));
            Assert.Empty(cancel.List()); Assert.False(child!.HasExited);
            Assert.Equal(Proto.CreationPhase.CreationCancelling, cancel.CancelCreate("barrier", ticket));
            release.Set(); Assert.NotNull(await create.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.True(SpinWait.SpinUntil(() => cancel.QueryCreate("barrier", ticket) == Proto.CreationPhase.CreationUnknown, 10000));
            Assert.True(child.WaitForExit(5000)); Assert.Empty(cancel.List());
        }
        finally
        {
            release.Set();
            await create.WaitAsync(TimeSpan.FromSeconds(20)); // leave barrier before disposing its events
            using var cleanup = PtyHostClient.Connect(_appId);
            Assert.True(SpinWait.SpinUntil(() => cleanup.CancelCreate("barrier", ticket) == Proto.CreationPhase.CreationUnknown, 15000));
            child?.Dispose();
        }
    }

    [Fact]
    public void Hello_RejectsProtocolMismatch()
    {
        var ok = _server.Dispatch(new Agwinterm.Pty.Proto.Request
        { Hello = new Agwinterm.Pty.Proto.Hello { Protocol = PtyHostServer.ProtocolVersion } });
        Assert.True(ok.Ok);
        var bad = _server.Dispatch(new Agwinterm.Pty.Proto.Request
        { Hello = new Agwinterm.Pty.Proto.Hello { Protocol = 999 } });
        Assert.Contains("protocol mismatch", bad.Error);
        var empty = _server.Dispatch(new Agwinterm.Pty.Proto.Request
        { Hello = new Agwinterm.Pty.Proto.Hello() });   // protocol 0 = absent in proto3
        Assert.Contains("protocol mismatch", empty.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DetachedSession_OnTheBundledConpty_HasItsQueriesAnswered(bool withTheme)
    {
        // The bundled ConPTY forwards the child's queries; with no client attached, the host answers them
        // (#339): the DSR always, the color with the theme from the pane's environment, else not at all.
        string appId = "agwinterm-test-" + Guid.NewGuid().ToString("N")[..8];
        using var server = new PtyHostServer(appId, ConPtyApi.Resolve(bundled: true, ConPtyConnectionTests.BundledDir), ghosttyOnly: false);
        using var client = PtyHostClient.Connect(appId);
        string outFile = Path.Combine(Path.GetTempPath(), appId + "-dsr.txt");
        try
        {
            string id = client.Create(Guid.NewGuid().ToString(), 100, 24, "powershell.exe", ConPtyConnectionTests.DsrProbeArgs(outFile),
                env: ConPtyConnectionTests.ThemeEnv(withTheme), verbatim: false);
            Assert.Equal(ConPtyConnectionTests.ExpectedDsrProbe(withTheme), ConPtyConnectionTests.ReadDsrProbe(outFile));
            client.Kill(id);
        }
        finally { File.Delete(outFile); }
    }

    [Fact]
    public void Resize_ReachesTheSession()
    {
        using var client = PtyHostClient.Connect(_appId);
        string id = client.Create(Guid.NewGuid().ToString(), 80, 24, "cmd.exe", new[] { "/q" }, verbatim: true);
        client.Resize(id, 132, 40);
        var info = Assert.Single(client.List());
        Assert.Equal((132, 40), (info.Cols, info.Rows));
        client.Kill(id);
    }

    [Fact]
    public void DuplicateCreate_IsRefused()
    {
        using var client = PtyHostClient.Connect(_appId);
        string id = client.Create("fixed-id", 80, 24, "cmd.exe", new[] { "/q" }, verbatim: true);
        Assert.Throws<InvalidOperationException>(() => client.Create("fixed-id", 80, 24, "cmd.exe", new[] { "/q" }));
        client.Kill(id);
    }

    [Fact]
    public async Task Shutdown_KillsSessionsAndCompletes()
    {
        var server = new PtyHostServer(_appId + "-sd", null, ghosttyOnly: false);
        using var client = PtyHostClient.Connect(_appId + "-sd");
        client.Create(Guid.NewGuid().ToString(), 80, 24, "cmd.exe", new[] { "/q" }, verbatim: true);
        client.Shutdown();
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
