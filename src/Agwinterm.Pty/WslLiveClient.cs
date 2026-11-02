using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>Local WSL control process. Never a PTY host; terminal transport remains Rust.</summary>
public sealed class WslLiveClient
{
    public sealed record Location(string Distribution, string User, string Cwd, string Ledger,
        string Helper, string SocketDirectory);
    private static readonly JsonSerializerOptions Wire = new() { PropertyNameCaseInsensitive = true };

    private static string Source(string name)
    {
        using var stream = typeof(WslLiveClient).Assembly.GetManifestResourceStream("Agwinterm.Pty.WslLive." + name)
            ?? throw new InvalidOperationException("Missing embedded Linux helper: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async Task<Location> PrepareAsync(ShellProfile profile, string appId, string? cwd, CancellationToken ct)
    {
        // Same application/profile endpoint is stable; distro and user each have their own filesystem.
        string endpoint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(appId + "\0" + profile.Name)))[..24];
        var args = WslIntegration.Selectors(profile.Args).ToList();
        string? directory = cwd ?? WslIntegration.ProfileDirectory(profile);
        if (!string.IsNullOrWhiteSpace(directory)) args.AddRange(["--cd", directory]);
        return await InstallAsync(args, endpoint, ct).ConfigureAwait(false);
    }

    public async Task<Location> RefreshAsync(Location saved, CancellationToken ct)
    {
        var current = await InstallAsync(WslLiveProfiles.ResolvedSelectors(saved.Distribution, saved.User).ToList(),
            saved.Ledger.Split('/').Last(), ct).ConfigureAwait(false);
        if (current.Distribution != saved.Distribution || current.User != saved.User || current.Ledger != saved.Ledger
            || current.SocketDirectory != saved.SocketDirectory)
            throw new InvalidOperationException("Helper update resolved a different saved endpoint");
        return current;
    }

    private static async Task<Location> InstallAsync(List<string> args, string endpoint, CancellationToken ct)
    {
        var modules = new[] { "zmx_helper.py", "zmx_exact.py", "zmx_ledger.py", "zmx_readiness.py" }
            .ToDictionary(n => n, n => Convert.ToBase64String(Encoding.UTF8.GetBytes(Source(n))));
        args.AddRange(["--exec", "python3", "-B", "-c", Source("zmx_install.py")]);
        var result = await RequestAsync(args, new { endpoint, modules }, ct).ConfigureAwait(false);
        if (result.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
        var location = result.Deserialize<Location>(Wire) ?? throw new InvalidOperationException("Invalid WSL location");
        _ = WslLiveProfiles.ResolvedSelectors(location.Distribution, location.User);
        if (!WslLiveValidation.LinuxPath(location.Helper) || !WslLiveValidation.LinuxPath(location.Ledger)
            || !WslLiveValidation.LinuxPath(location.Cwd) || !WslLiveValidation.LinuxPath(location.SocketDirectory))
            throw new InvalidOperationException("Invalid resolved Linux paths");
        return location;
    }

    public async Task<JsonElement> InvokeAsync(Location location, object request, CancellationToken ct)
    {
        string[] args = [.. WslLiveProfiles.ResolvedSelectors(location.Distribution, location.User),
            "--exec", "python3", "-B", location.Helper];
        var response = await RequestAsync(args, request, ct).ConfigureAwait(false);
        if (!response.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new WslRecoveryException(response.TryGetProperty("state", out var state) ? state.GetString() ?? "uncertain" : "uncertain",
                response.TryGetProperty("error", out var error) ? error.GetString() ?? "Helper failed" : "Invalid helper response");
        return response.GetProperty("result").Clone();
    }

    public static string[] AttachmentArgs(Location location, object request)
    {
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(request);
        if (raw.Length > 16000) throw new ArgumentException("Attachment request exceeds Windows command-line limit");
        return [.. WslLiveProfiles.ResolvedSelectors(location.Distribution, location.User), "--exec", "python3", "-B",
            location.Helper, "--request", Convert.ToBase64String(raw)];
    }

    private static async Task<JsonElement> RequestAsync(IEnumerable<string> args, object request, CancellationToken ct)
    {
        var start = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("ZMX_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(key);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start WSL helper");
        // Drain diagnostics without accumulating unbounded output or waiting for a lifetime lease to end.
        _ = DrainAsync(process.StandardError);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token).ConfigureAwait(false);
        process.StandardInput.Close();
        var line = new StringBuilder();
        var buffer = new char[1024];
        while (line.Length <= 4194304)
        {
            int count = await process.StandardOutput.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
            if (count == 0) throw new IOException("WSL helper exited before its response");
            int newline = Array.IndexOf(buffer, '\n', 0, count);
            line.Append(buffer, 0, newline < 0 ? count : newline);
            if (newline >= 0)
            {
                using var document = JsonDocument.Parse(line.ToString());
                return document.RootElement.Clone();
            }
        }
        throw new IOException("WSL helper response exceeds limit");
        // Dispose closes Windows handles, never kills the independent WSL lease.
        // A timeout is uncertain; callers reconcile the same operation ID.
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try { var buffer = new char[1024]; while (await reader.ReadAsync(buffer) > 0) { } }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }
}
