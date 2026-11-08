using System.Diagnostics;
using System.Text;

namespace Agwinterm.Pty;

public sealed record AgtermSshTarget(string Distribution, string User, string Destination, string? AgentSocket = null, string? OriginSocket = null)
{
    public void Validate()
    {
        _ = WslLiveProfiles.ResolvedSelectors(Distribution, User);
        if (string.IsNullOrEmpty(Destination) || Destination.Length > 256 || Destination.StartsWith('-')
            || !Destination.All(c => char.IsAsciiLetterOrDigit(c) || "@._-:[]".Contains(c)))
            throw new ArgumentException("Invalid SSH destination");
        if (AgentSocket is not null && !AgtermRemoteDiscovery.OriginPath(AgentSocket))
            throw new ArgumentException("SSH agent socket must be an absolute Linux path");
        if (OriginSocket is not null && !AgtermRemoteDiscovery.OriginPath(OriginSocket))
            throw new ArgumentException("Origin control socket must be an absolute path");
    }
}

/// <summary>Read-only discovery transport. No terminal attachment or remote lifecycle commands.</summary>
public static class AgtermSshDiscoveryClient
{
    public static async Task<AgtermRemoteInventory> DiscoverAsync(AgtermSshTarget target, CancellationToken ct = default)
    {
        var result = await RunAsync(StartInfo(target), TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        return AgtermRemoteDiscovery.Decode(result.ExitCode, result.Stdout);
    }

    internal static ProcessStartInfo StartInfo(AgtermSshTarget target)
        => CommandInfo(target, false, string.Join(' ', new[] { "/bin/sh", "-c",
            "PATH=\"$PATH:$HOME/.local/bin:/usr/local/bin:/opt/homebrew/bin\"; export PATH; exec agtermctl zmx tree --json" +
            (target.OriginSocket is null ? "" : " --socket " + QuoteAccountShell(target.OriginSocket)) }.Select(QuoteAccountShell)));

    internal static ProcessStartInfo CommandInfo(AgtermSshTarget target, bool terminal, string remoteCommand)
    {
        target.Validate();
        var start = new ProcessStartInfo("wsl.exe");
        foreach (string arg in WslLiveProfiles.ResolvedSelectors(target.Distribution, target.User)) start.ArgumentList.Add(arg);
        start.ArgumentList.Add("--exec");
        start.ArgumentList.Add("/usr/bin/env");
        // This is the attachment PTY, not the already-running origin workload.
        // OpenSSH sends TERM with the PTY request without requiring AcceptEnv.
        if (terminal) start.ArgumentList.Add("TERM=" + Agwinterm.Core.TerminalIdentity.Term);
        if (target.AgentSocket is not null) start.ArgumentList.Add("SSH_AUTH_SOCK=" + target.AgentSocket);
        foreach (string arg in new[] { "ssh", terminal ? "-tt" : "-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
            "-o", "UpdateHostKeys=no", "-o", "ConnectTimeout=5", "-o", "ConnectionAttempts=1",
            "-o", "ClearAllForwardings=yes", "-o", "PermitLocalCommand=no" })
            start.ArgumentList.Add(arg);
        if (terminal)
            foreach (string arg in new[] { "-o", "ServerAliveInterval=5", "-o", "ServerAliveCountMax=2" })
                start.ArgumentList.Add(arg);
        start.ArgumentList.Add(target.Destination);
        start.ArgumentList.Add(remoteCommand);
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("ZMX_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(key);
        return start;
    }

    // Unlike POSIX single quotes alone, this also preserves backslashes in fish's single quotes.
    internal static string QuoteAccountShell(string value)
    {
        var result = new StringBuilder("'");
        foreach (char c in value)
            result.Append(c switch { '\\' => "'\\\\'", '\'' => "'\\''", '!' => "'\\!'", _ => c.ToString() });
        return result.Append('\'').ToString();
    }

    internal sealed record Result(int ExitCode, string Stdout);

    internal static async Task<Result> RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct, string? input = null)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.StandardOutputEncoding = new UTF8Encoding(false, true);
        start.StandardErrorEncoding = new UTF8Encoding(false, true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("Cannot start remote discovery");
        using var registration = deadline.Token.Register(() => Stop(process));
        // Both pipes must be read concurrently. stderr is bounded but never echoed as trusted text.
        Task<string> stdout = ReadAsync(process.StandardOutput.BaseStream, AgtermRemoteDiscovery.MaxBytes, deadline.Token);
        Task<string> stderr = ReadAsync(process.StandardError.BaseStream, 64 * 1024, deadline.Token);
        Task exited = process.WaitForExitAsync(deadline.Token);
        async Task WriteInput()
        {
            try { if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token).ConfigureAwait(false); }
            finally { process.StandardInput.Close(); }
        }
        Task written = WriteInput();
        try
        {
            var pending = new List<Task> { stdout, stderr, exited, written };
            while (pending.Count != 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
            }
            return new(process.ExitCode, await stdout.ConfigureAwait(false));
        }
        catch
        {
            deadline.Cancel();
            Stop(process);
            try { await Task.WhenAll(stdout, stderr, exited, written).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task<string> ReadAsync(Stream stream, int limit, CancellationToken ct)
    {
        using var result = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0) return new UTF8Encoding(false, true).GetString(result.GetBuffer(), 0, (int)result.Length);
            if (result.Length + count > limit) throw new IOException("Remote discovery exceeds output limit");
            result.Write(buffer, 0, count);
        }
    }
}
