namespace Agwinterm.Pty;

/// <summary>
/// Where terminal sessions live. The UI creates every session through exactly one of these, chosen
/// at startup from the <c>session-host</c> config key — never via <c>new TerminalSession</c>
/// directly — so the pty-host server backend (#105, Phase 2) can slot in behind the same seam.
/// </summary>
public interface ISessionBackend
{
    /// <summary>Stable name for diagnostics/toasts ("in-process", "server").</summary>
    string Name { get; }

    /// <summary>Create a session sized <paramref name="cols"/>×<paramref name="rows"/> (not yet
    /// started). <paramref name="id"/> is the PANE id — for the server backend it names the hosted
    /// session, which is the adoption key after a UI restart (in-process ignores it).</summary>
    ISession Create(string id, int cols, int rows);
}

/// <summary>Today's model: the UI process owns the ConPTY (a plain <see cref="TerminalSession"/>).</summary>
public sealed class InProcessSessionBackend : ISessionBackend
{
    public static readonly InProcessSessionBackend Instance = new();
    public string Name => "in-process";
    public ISession Create(string id, int cols, int rows)
    {
        var session=new TerminalSession(cols,rows);
        if(session.Emulator is Agwinterm.Core.GhosttyTerminalCore)
            session.EnablePresentation();
        return session;
    }
}

public static class SessionBackends
{
    /// <summary>Resolve hosted Ghostty or direct local operation. Old server-rust settings migrate to the same owner host.</summary>
    public static ISessionBackend Resolve(string? configured, string appId, string? exePath, string? conpty = null,
        string? replicaCore = null)
    {
        // Always explicit: a pty-host started without the flag keeps the inbox conhost.
        string conptyArg = conpty is null ? "" : conpty == "inbox" ? " --conpty inbox" : " --conpty bundled";
        if (string.Equals(configured, "server", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(configured, "server-rust", StringComparison.OrdinalIgnoreCase))
        {
            string serverId = appId + "-owner-v1";
            return new ServerSessionBackend(serverId, exePath, $"--pty-host --pipe \"{serverId}\"{conptyArg}");
        }
        return InProcessSessionBackend.Instance;
    }

    // WSL and remote live transports use the same Ghostty owner host.
    public static ServerSessionBackend ResolveLive(Agwinterm.Core.TerminalConfig config, string appId, string? exePath)
        => (ServerSessionBackend)Resolve("server",
            appId, exePath, config.Conpty, config.EmulatorCore);
}
