using System.Text;

namespace Agwinterm.Pty;

public interface IAgtermPresentationOriginHost
{
    AgtermOriginPageLease OpenOverlayPage(string session, string token, System.Text.Json.JsonElement args, string account)
        => throw new IOException("Origin pages unavailable");
    AgtermOverlayJob OpenOverlayJob(string session, string job, System.Text.Json.JsonElement args, string account)
        => throw new IOException("Origin jobs unavailable");
    AgtermOriginPresentationLease OpenPresentation(string session, System.Text.Json.JsonElement args,
        string account, AgtermPresentationHello hello);
}
public sealed record AgtermOriginPageLease(AgtermPageGrant Grant,
    Func<string, CancellationToken, Task<AgtermPageResource>> Resource,
    Func<string, CancellationToken, Task<string>> Bridge);
public sealed record AgtermOriginPresentationLease(AgtermPresentationBroker.Subscription Subscription, Func<bool> IsCurrent,
    Func<AgtermPresentationFrame, CancellationToken, Task>? Receive = null);

public static class AgtermPresentationStream
{
    public static async Task<string?> ReadLineAsync(TextReader reader, CancellationToken ct)
    {
        // StreamReader may already buffer bytes after the control request. Keep a single reader.
        var line = new StringBuilder();
        char[] character = new char[1];
        while (await reader.ReadAsync(character.AsMemory(), ct).ConfigureAwait(false) != 0)
        {
            if (character[0] == '\n') return line.ToString();
            if (line.Length >= AgtermPresentationProtocol.MaxFrameBytes) throw new IOException("Presentation line exceeds limit");
            line.Append(character[0]);
        }
        if (line.Length != 0) throw new IOException("Truncated presentation frame");
        return null;
    }

    public static async Task ServeAsync(AgtermPresentationBroker.Subscription subscription,
        TextReader reader, StreamWriter writer, Action abort, Func<bool> alive, CancellationToken ct,
        Func<AgtermPresentationFrame, CancellationToken, Task>? receive = null)
    {
        using (subscription)
        using (var stopped = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            long lastAck = Environment.TickCount64;
            async Task Read()
            {
                while (await ReadLineAsync(reader, stopped.Token).ConfigureAwait(false) is { } line)
                {
                    var frame = AgtermPresentationProtocol.Decode(Encoding.UTF8.GetBytes(line));
                    if (frame.Generation != subscription.Generation) continue;
                    if (frame.Kind == "ack") Interlocked.Exchange(ref lastAck, Environment.TickCount64);
                    else if (frame.Kind == "presenter.acquire") subscription.AcquirePresenter();
                    else if (frame.Kind is "ask.resolve" or "ask.rejected" or "overlay.rejected" or "overlay.closed" && receive is not null)
                        await receive(frame, stopped.Token).WaitAsync(stopped.Token).ConfigureAwait(false);
                    else if (frame.Kind == "ping")
                    {
                        if (!subscription.Offer("ack", default)) throw new IOException("Presentation subscriber stalled");
                    }
                    else throw new IOException("Presentation operation is not authorized");
                }
            }
            async Task Write()
            {
                await foreach (var frame in subscription.ReadAllAsync(stopped.Token).ConfigureAwait(false))
                {
                    var text = Encoding.UTF8.GetString(AgtermPresentationProtocol.Encode(frame));
                    // Drain the real write before writer disposal; never abandon an overlapped pipe write.
                    Task writing = writer.WriteAsync(text.AsMemory(), CancellationToken.None);
                    try { await writing.WaitAsync(TimeSpan.FromSeconds(5), stopped.Token).ConfigureAwait(false); }
                    catch { abort(); try { await writing.ConfigureAwait(false); } catch { } throw; }
                }
            }
            async Task Heartbeat()
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stopped.Token).ConfigureAwait(false);
                    if (Environment.TickCount64 - Interlocked.Read(ref lastAck) > 30000 || !alive()
                        || !subscription.Offer("ping", default)) throw new IOException("Presentation origin unavailable or subscriber stale");
                }
            }
            var tasks = new[] { Read(), Write(), Heartbeat() };
            try { await await Task.WhenAny(tasks).ConfigureAwait(false); }
            finally
            {
                stopped.Cancel();
                abort();
                try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
            }
        }
    }
}
