using System.Text;
using System.Threading.Channels;

namespace Agwinterm.Pty;

public sealed record AgtermPageResource(Stream Body, string MimeType);

/// <summary>One claimed file page. Delegates are bound to its origin grant, never to viewer paths or targets.</summary>
public static class AgtermPageServer
{
    public static async Task ServeAsync(TextReader reader, StreamWriter writer, Action abort,
        Func<string, CancellationToken, Task<AgtermPageResource>> resource,
        Func<string, CancellationToken, Task<string>> bridge, CancellationToken cancellation)
    {
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var output = Channel.CreateBounded<AgtermPageFrame>(new BoundedChannelOptions(8)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var gate = new object();
        var pending = new Dictionary<long, (string Kind, CancellationTokenSource Cancel, Task Work)>();
        int resources = 0, bridges = 0;
        ValueTask Send(AgtermPageFrame frame) => output.Writer.WriteAsync(frame, stopped.Token);

        async Task Work(AgtermPageFrame frame, CancellationTokenSource request)
        {
            // Admission inserts the worker before it can finish, even for synchronous delegates.
            await Task.Yield();
            try
            {
                try
                {
                    if (frame.Kind == "resource")
                    {
                        var source = await resource(frame.Text!, request.Token).ConfigureAwait(false);
                        await using (var body = source.Body)
                        {
                            request.Token.ThrowIfCancellationRequested();
                            await Send(new("head", frame.Id, source.MimeType)).ConfigureAwait(false);
                            var buffer = new byte[AgtermPageProtocol.MaxChunkBytes];
                            int count;
                            while ((count = await body.ReadAsync(buffer, request.Token).ConfigureAwait(false)) != 0)
                            {
                                request.Token.ThrowIfCancellationRequested();
                                await Send(new("chunk", frame.Id, Data: buffer[..count])).ConfigureAwait(false);
                            }
                        }
                        request.Token.ThrowIfCancellationRequested();
                        await Send(new("end", frame.Id)).ConfigureAwait(false);
                    }
                    else
                    {
                        string response = await bridge(frame.Text!, request.Token).ConfigureAwait(false);
                        request.Token.ThrowIfCancellationRequested();
                        await Send(new("reply", frame.Id, response)).ConfigureAwait(false);
                    }
                }
                catch (Exception) when (!stopped.IsCancellationRequested)
                {
                    // Origin exceptions can contain local paths or capability tokens.
                    await Send(new("failure", frame.Id, request.IsCancellationRequested ? "cancelled" : "request failed")).ConfigureAwait(false);
                }
            }
            finally
            {
                lock (gate)
                {
                    pending.Remove(frame.Id);
                    if (frame.Kind == "resource") resources--; else bridges--;
                    request.Dispose();
                }
            }
        }

        async Task Read()
        {
            while (await AgtermPresentationStream.ReadLineAsync(reader, stopped.Token).ConfigureAwait(false) is { } line)
            {
                var frame = AgtermPageProtocol.Decode(Encoding.UTF8.GetBytes(line));
                bool busy = false;
                lock (gate)
                {
                    if (frame.Kind == "cancel")
                    {
                        if (pending.TryGetValue(frame.Id, out var active)) active.Cancel.Cancel();
                        continue;
                    }
                    if (frame.Kind is not ("resource" or "bridge")) throw new IOException("Unexpected page request direction");
                    if (pending.ContainsKey(frame.Id)) throw new IOException("Duplicate active page request ID");
                    busy = frame.Kind == "resource" ? resources == AgtermPageProtocol.ResourceLimit : bridges == AgtermPageProtocol.BridgeLimit;
                    if (!busy)
                    {
                        var request = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
                        if (frame.Kind == "resource") resources++; else bridges++;
                        pending.Add(frame.Id, (frame.Kind, request, Work(frame, request)));
                    }
                }
                if (busy) await Send(new("failure", frame.Id, "too many requests in flight")).ConfigureAwait(false);
            }
        }

        async Task Write()
        {
            await foreach (var frame in output.Reader.ReadAllAsync(stopped.Token).ConfigureAwait(false))
            {
                string text = Encoding.UTF8.GetString(AgtermPageProtocol.Encode(frame));
                Task writing = writer.WriteAsync(text.AsMemory(), CancellationToken.None);
                try { await writing.WaitAsync(TimeSpan.FromSeconds(5), stopped.Token).ConfigureAwait(false); }
                catch { abort(); try { await writing.ConfigureAwait(false); } catch { } throw; }
            }
        }

        var reading = Read();
        var writing = Write();
        try { await await Task.WhenAny(reading, writing).ConfigureAwait(false); }
        finally
        {
            stopped.Cancel();
            abort();
            output.Writer.TryComplete();
            try { await Task.WhenAll(reading, writing).ConfigureAwait(false); } catch { }
            Task[] workers;
            lock (gate) workers = pending.Values.Select(x => x.Work).ToArray();
            try { await Task.WhenAll(workers).ConfigureAwait(false); } catch { }
        }
    }
}
