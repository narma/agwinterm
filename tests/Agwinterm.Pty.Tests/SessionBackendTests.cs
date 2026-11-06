using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class SessionBackendTests
{
    [Theory]
    [InlineData("", "ghostty")]
    [InlineData("emulator-core = ghostty", "ghostty")]
    [InlineData("emulator-core = rust", "ghostty")]
    [InlineData("emulator-core = managed", "ghostty")]
    public void LiveTransport_UsesHostCompatibleWithSelectedCore(string setting, string expectedCore)
    {
        var config = Agwinterm.Core.TerminalConfig.Parse("session-host = server-rust\n" + setting);
        using var live = SessionBackends.ResolveLive(config, "isolated-live", null);
        Assert.Equal("server", config.ConfiguredSessionHost);
        Assert.Equal("server", live.Name);
        Assert.Equal(expectedCore, live.ReplicaCore);
        Assert.Contains("--pipe \"isolated-live-owner-v1\"", live.SpawnArgs);
        Assert.Equal("server",
            SessionBackends.Resolve(config.SessionHost, "isolated-live", null).Name);
    }

    [Fact]
    public void SavedLiveBinding_UsesGhosttyHostEvenWhenOrdinaryHostIsInProcess()
    {
        var config = new Agwinterm.Core.TerminalConfig { SessionHost = "in-process" };
        using var live = SessionBackends.ResolveLive(config, "saved-live", null);
        Assert.Equal("server", live.Name);
        Assert.Equal("ghostty", live.ReplicaCore);
        Assert.Equal("in-process", config.SessionHost);
    }

    [Fact]
    public void InProcessBackend_CreatesWorkingSessions()
    {
        using ISession s = InProcessSessionBackend.Instance.Create("t", 20, 5);
        Assert.Equal(20, s.Cols);
        Assert.Equal(5, s.Rows);
        // The handle drives the emulator through the interface alone (what a Phase-2 replica must honor).
        s.Inject("hello"u8);
        Assert.Equal("hello", s.SnapshotRow(0).TrimEnd());
        s.Resize(30, 6);
        Assert.Equal(30, s.Cols);
        Assert.False(s.HasExited);   // never started — no phantom exit
    }

    [Fact]
    public void Resolve_PicksBackendByConfiguredValue()
    {
        Assert.Same(InProcessSessionBackend.Instance, SessionBackends.Resolve("in-process", "x", null));
        Assert.Same(InProcessSessionBackend.Instance, SessionBackends.Resolve(null, "x", null));
        Assert.IsType<ServerSessionBackend>(SessionBackends.Resolve("server", "x", null));
        Assert.IsType<ServerSessionBackend>(SessionBackends.Resolve("SERVER", "x", null));
    }

    [Fact]
    public void Resolve_ServerRust_MigratesToGhosttyHost()
    {
        var b = SessionBackends.Resolve("server-rust", "x", null);
        Assert.IsType<ServerSessionBackend>(b);
        Assert.Equal("server", b.Name);
        // Case-insensitive, like the other values.
        Assert.Equal("server", SessionBackends.Resolve("SERVER-RUST", "x", null).Name);
    }

    [Fact]
    public void Resolve_PassesTheConptyKeyToThePtyHosts()
    {
        // A pty-host picks its ConPTY once, when it starts (#339), and without the flag keeps the inbox one.
        Assert.Equal("--pty-host --pipe \"x-owner-v1\" --conpty bundled", ((ServerSessionBackend)SessionBackends.Resolve("server-rust", "x", null, "bundled")).SpawnArgs);
        Assert.Equal("--pty-host --pipe \"x-owner-v1\" --conpty inbox", ((ServerSessionBackend)SessionBackends.Resolve("server-rust", "x", null, "inbox")).SpawnArgs);
        Assert.Equal("--pty-host --pipe \"x-owner-v1\" --conpty bundled", ((ServerSessionBackend)SessionBackends.Resolve("server", "x", null, "bundled")).SpawnArgs);
    }

    [Fact]
    public void ServerBackend_WithNoHostAndNoExe_FailsAtCreate()
    {
        // Failure surfaces at Create — the one place the UI can catch it and fall back in-process.
        using var backend = new ServerSessionBackend("agwinterm-test-nohost-" + Guid.NewGuid().ToString("N")[..8], exePath: null, singleOwner: false);
        Assert.Throws<InvalidOperationException>(() => backend.Create("t3", 80, 24));
    }

    [Fact]
    public void ControlServer_AcceptsAnyISession()
    {
        // The single-session ControlServer ctor is part of the seam: it must take the interface.
        using ISession s = InProcessSessionBackend.Instance.Create("t2", 80, 24);
        using var server = new ControlServer(s, "agwinterm-test-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.NotNull(server);
    }
}
