using System.Diagnostics;
using System.Text;

namespace Agwinterm.Pty;

public sealed class AgtermPageTransport : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _stop = new();
    public AgtermPageClient Client { get; }
    public Task Completion { get; }
    internal static ProcessStartInfo StartInfo(AgtermRemoteBinding binding, string token)
    {
        if (!binding.IsValid || binding.PresentationVersion is not > 0 || token.Length is < 1 or > 256 || token.Any(char.IsControl))
            throw new ArgumentException("Invalid page channel binding");
        string command = "PATH=\"$PATH:$HOME/.local/bin:/usr/local/bin:/opt/homebrew/bin\"; export PATH; exec agtermctl session overlay page-serve "
            + AgtermSshDiscoveryClient.QuoteAccountShell(token)
            + (binding.Target.OriginSocket is null ? "" : " --socket " + AgtermSshDiscoveryClient.QuoteAccountShell(binding.Target.OriginSocket));
        var start = AgtermSshDiscoveryClient.CommandInfo(binding.Target, false,
            string.Join(' ', new[] { "/bin/sh", "-c", command }.Select(AgtermSshDiscoveryClient.QuoteAccountShell)));
        start.UseShellExecute = false; start.CreateNoWindow = true;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.StandardOutputEncoding = new UTF8Encoding(false, true);
        start.StandardInputEncoding = new UTF8Encoding(false);
        return start;
    }
    public AgtermPageTransport(AgtermRemoteBinding binding, string token)
    {
        _process = Process.Start(StartInfo(binding, token)) ?? throw new IOException("Page transport unavailable");
        _process.StandardInput.AutoFlush = true;
        Client = new(_process.StandardOutput, _process.StandardInput, Stop);
        Completion = Monitor();
    }
    private void Stop()
    {
        try { if (!_process.HasExited) _process.Kill(true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    private async Task Monitor()
    {
        async Task Errors()
        {
            byte[] buffer = new byte[4096]; int total = 0, count;
            while ((count = await _process.StandardError.BaseStream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false)) != 0)
                if ((total += count) > 65536) throw new IOException("Page stderr limit exceeded");
            await Task.Delay(Timeout.Infinite, _stop.Token).ConfigureAwait(false);
        }
        var errors = Errors();
        try { await await Task.WhenAny(Client.Completion, errors).ConfigureAwait(false); }
        finally
        {
            _stop.Cancel(); Client.Close(); Stop();
            try { await errors.ConfigureAwait(false); } catch { }
            await Client.DisposeAsync().ConfigureAwait(false);
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
    }
    public async ValueTask DisposeAsync()
    {
        Client.Close();
        try { await Completion.ConfigureAwait(false); } catch { /* Completion exposes the channel failure. */ }
        _process.Dispose();
    }
}
