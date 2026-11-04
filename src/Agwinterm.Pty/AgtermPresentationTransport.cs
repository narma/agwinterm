using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace Agwinterm.Pty;

/// <summary>One bounded non-PTY presentation process. Never owns or stops a terminal process.</summary>
public sealed class AgtermPresentationTransport
{
    internal static ProcessStartInfo StartInfo(AgtermRemoteBinding binding)
    {
        if (!binding.IsValid || binding.PresentationVersion is not > 0)
            throw new ArgumentException("Presentation requires a valid capable binding");
        string command = "PATH=\"$PATH:$HOME/.local/bin:/usr/local/bin:/opt/homebrew/bin\"; export PATH; exec agtermctl zmx present "
            + AgtermSshDiscoveryClient.QuoteAccountShell(binding.OriginSession)
            + (binding.Target.OriginSocket is null ? "" : " --socket " + AgtermSshDiscoveryClient.QuoteAccountShell(binding.Target.OriginSocket));
        return AgtermSshDiscoveryClient.CommandInfo(binding.Target, false,
            string.Join(' ', new[] { "/bin/sh", "-c", command }.Select(AgtermSshDiscoveryClient.QuoteAccountShell)));
    }

    public Task RunAsync(AgtermRemoteBinding binding, string[] implemented,
        Func<AgtermPresentationFrame, CancellationToken, Task> receive, CancellationToken ct,
        string mode = "mirror", Action<AgtermPresentationSender>? initialized = null) =>
        RunAsync(StartInfo(binding), implemented, receive, ct, mode: mode, initialized: initialized);

    internal async Task RunAsync(ProcessStartInfo start, string[] implemented,
        Func<AgtermPresentationFrame, CancellationToken, Task> receive, CancellationToken ct,
        TimeSpan? handshake = null, TimeSpan? silence = null, TimeSpan? ping = null,
        string mode = "mirror", Action<AgtermPresentationSender>? initialized = null)
    {
        byte[] initialHello = AgtermPresentationProtocol.Encode(new("hello", 0, 0,
            JsonSerializer.SerializeToElement(new { version = 1, kinds = implemented, mode })));
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("Presentation process did not start");
        void Stop()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        using var registration = stopped.Token.Register(Stop);
        var outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(AgtermPresentationProtocol.MaxPendingFrames)
            { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        var incoming = Channel.CreateBounded<AgtermPresentationFrame>(new BoundedChannelOptions(AgtermPresentationProtocol.MaxPendingFrames)
            { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = true });
        var state = new AgtermPresentationConnection();
        long launch = state.Begin(1, implemented, DateTimeOffset.UtcNow);
        object gate = new();
        void Send(AgtermPresentationFrame frame)
        {
            if (!outgoing.Writer.TryWrite(AgtermPresentationProtocol.Encode(frame))) throw new IOException("Presentation writer stalled");
        }
        var sender = new AgtermPresentationSender((kind, body) =>
        {
            lock (gate)
            {
                if (stopped.IsCancellationRequested || state.Generation is not { } gen) throw new IOException("Presentation response channel closed");
                Send(new(kind, gen, 0, body));
            }
        });
        initialized?.Invoke(sender);
        outgoing.Writer.TryWrite(initialHello);
        async Task Read()
        {
            var decoder = new AgtermPresentationLines();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int count = await process.StandardOutput.BaseStream.ReadAsync(buffer, stopped.Token).ConfigureAwait(false);
                if (count == 0) { decoder.Complete(); throw new IOException("Presentation stream ended"); }
                decoder.Feed(buffer.AsSpan(0, count), frame =>
                {
                    AgtermPresentationDisposition disposition;
                    lock (gate) disposition = state.Accept(launch, frame, DateTimeOffset.UtcNow);
                    if (disposition == AgtermPresentationDisposition.Snapshot)
                        frame = frame with { Body = AgtermPresentationProtocol.NegotiatedSnapshot(frame.Body, state.Kinds) };
                    if (disposition == AgtermPresentationDisposition.Ignored) return;
                    if (frame.Kind == "hello") { sender.Mode = AgtermPresentationProtocol.Hello(frame).Mode; sender.Kinds = state.Kinds; }
                    if (frame.Kind == "presenter.granted") sender.Granted = sender.Mode == "presenter";
                    if (frame.Kind == "presenter.refused") sender.Granted = false;
                    if (frame.Kind == "ping") Send(new("ack", frame.Generation, 0, default));
                    if (disposition is AgtermPresentationDisposition.Snapshot or AgtermPresentationDisposition.Delta
                        && !incoming.Writer.TryWrite(frame)) throw new IOException("Presentation consumer stalled");
                });
            }
        }
        async Task Write()
        {
            await foreach (var bytes in outgoing.Reader.ReadAllAsync(stopped.Token).ConfigureAwait(false))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
                deadline.CancelAfter(handshake ?? TimeSpan.FromSeconds(5));
                await process.StandardInput.BaseStream.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        async Task DrainErrors()
        {
            byte[] buffer = new byte[4096];
            int total = 0, count;
            while ((count = await process.StandardError.BaseStream.ReadAsync(buffer, stopped.Token).ConfigureAwait(false)) != 0)
                if ((total += count) > 65536) throw new IOException("Presentation stderr exceeds limit");
            await Task.Delay(Timeout.Infinite, stopped.Token).ConfigureAwait(false);
        }
        async Task Deliver()
        {
            await foreach (var frame in incoming.Reader.ReadAllAsync(stopped.Token).ConfigureAwait(false))
                await receive(frame, stopped.Token).WaitAsync(stopped.Token).ConfigureAwait(false);
        }
        async Task Heartbeat()
        {
            var lastPing = DateTimeOffset.UtcNow;
            while (true)
            {
                await Task.Delay(100, stopped.Token).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                lock (gate)
                {
                    if (state.Expired(launch, now, handshake ?? TimeSpan.FromSeconds(5), silence ?? TimeSpan.FromSeconds(30)))
                        throw new IOException("Presentation handshake or heartbeat timed out");
                    if (state.Generation is { } gen && now - lastPing >= (ping ?? TimeSpan.FromSeconds(10)))
                    { Send(new("ping", gen, 0, default)); lastPing = now; }
                }
            }
        }
        var tasks = new[] { Read(), Write(), DrainErrors(), Deliver(), Heartbeat() };
        try { await await Task.WhenAny(tasks).ConfigureAwait(false); }
        finally
        {
            stopped.Cancel();
            sender.Granted = false;
            Stop();
            outgoing.Writer.TryComplete();
            incoming.Writer.TryComplete();
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
        }
    }
}
