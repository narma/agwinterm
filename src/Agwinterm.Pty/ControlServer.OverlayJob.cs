using System.IO.Pipes;
using System.Text.Json;

namespace Agwinterm.Pty;

public sealed partial class ControlServer
{
    private static async Task ServeOverlayJobAsync(AgtermOverlayJob job, StreamReader reader,
        StreamWriter writer, NamedPipeServerStream pipe, CancellationToken ct)
    {
        // Claim happened on the UI queue. The channel is independent of its presentation subscription.
        using var writeLock = new SemaphoreSlim(1);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Send(object value)
        {
            await writeLock.WaitAsync(stop.Token).ConfigureAwait(false);
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), stop.Token)
                .WaitAsync(TimeSpan.FromSeconds(5), stop.Token).ConfigureAwait(false); }
            finally { writeLock.Release(); }
        }
        var cancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Cancel() => cancel.TrySetResult();
        job.Cancelled += Cancel;
        if (job.Result is not null) Cancel();
        try
        {
            await Send(new { kind = "context", context = job.LaunchContext }).ConfigureAwait(false);
            async Task Read()
            {
                while (true)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(35));
                    var line = await AgtermPresentationStream.ReadLineAsync(reader, deadline.Token).ConfigureAwait(false);
                    if (line is null) return;
                    using var doc = JsonDocument.Parse(line);
                    AgtermRemoteDiscovery.RejectDuplicates(doc.RootElement);
                    switch (doc.RootElement.GetProperty("kind").GetString())
                    {
                        case "ping": await Send(new { kind = "ack" }).ConfigureAwait(false); break;
                        case "exit":
                            int code = doc.RootElement.GetProperty("code").GetInt32();
                            if (code is < 0 or > 255) throw new IOException("Invalid job exit");
                            job.Finish("exit " + code); return;
                        default: throw new IOException("Unsupported job report");
                    }
                }
            }
            async Task Cancellation()
            {
                await cancel.Task.WaitAsync(stop.Token).ConfigureAwait(false);
                await Send(new { kind = "cancel" }).ConfigureAwait(false);
                await Task.Delay(Timeout.Infinite, stop.Token).ConfigureAwait(false);
            }
            var tasks = new[] { Read(), Cancellation() };
            try { await await Task.WhenAny(tasks).ConfigureAwait(false); }
            finally { stop.Cancel(); pipe.Dispose(); try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { } }
        }
        finally { job.Cancelled -= Cancel; job.Finish("channel-lost"); }
    }
}
