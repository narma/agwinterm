using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Agwinterm.Pty;

public sealed partial class ControlServer
{
    internal Action<Exception>? PresentationStreamFailure { get; set; }
    private async Task<bool> TryPresentationStreamAsync(string line, NamedPipeServerStream pipe,
        StreamReader reader, StreamWriter writer, CancellationToken ct)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return false; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cmd", out var command)
                || command.ValueKind != JsonValueKind.String || command.GetString() is not ("zmx.export.present" or "zmx.export.job" or "zmx.export.page")) return false;
            try
            {
                if (Encoding.UTF8.GetByteCount(line) > 16384) throw new IOException("Presentation request exceeds limit");
                AgtermRemoteDiscovery.RejectDuplicates(root);
                foreach (var field in root.EnumerateObject())
                    if (field.Name is not ("cmd" or "window" or "target" or "args" or "job" or "token")) throw new IOException("Unknown presentation option");
                string window = root.GetProperty("window").GetString()!, session = root.GetProperty("target").GetString()!;
                if (!Guid.TryParseExact(window, "D", out _) || !Guid.TryParseExact(session, "D", out _)
                    || _windows?.Windows().Any(w => w.Open && w.Id == window) != true)
                    throw new IOException("Exact open window and session required");
                using var identity = WindowsIdentity.GetCurrent();
                string account = identity.User?.Value ?? throw new IOException("Missing Windows account identity");
                string? peerAccount = null;
                pipe.RunAsClient(() => { using var peer = WindowsIdentity.GetCurrent(true); peerAccount = peer?.User?.Value; });
                if (account != peerAccount)
                    throw new IOException("Presentation account mismatch");
                var args = root.GetProperty("args").Clone();
                _ = WslOriginExportRequest.Parse(args, null);
                if (_windows.ResolveWindow(window) is not IAgtermPresentationOriginHost host)
                    throw new IOException("Origin presentation unavailable");
                if (command.GetString() == "zmx.export.page")
                {
                    var page = host.OpenOverlayPage(session, root.GetProperty("token").GetString()!, args, account);
                    using var stopped = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    void Ended()
                    {
                        // Close can already have captured the handler when teardown unsubscribes it.
                        try { stopped.Cancel(); } catch (ObjectDisposedException) { }
                    }
                    page.Grant.Ended += Ended;
                    try
                    {
                        if (!page.Grant.IsAlive) stopped.Cancel();
                        await AgtermPageServer.ServeAsync(reader, writer, pipe.Dispose, page.Resource, page.Bridge, stopped.Token).ConfigureAwait(false);
                    }
                    finally { page.Grant.Ended -= Ended; page.Grant.Close(); }
                    return true;
                }
                if (command.GetString() == "zmx.export.job")
                {
                    var job = host.OpenOverlayJob(session, root.GetProperty("job").GetString()!, args, account);
                    await ServeOverlayJobAsync(job, reader, writer, pipe, ct).ConfigureAwait(false);
                    return true;
                }
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshake.CancelAfter(TimeSpan.FromSeconds(5));
                var helloLine = await AgtermPresentationStream.ReadLineAsync(reader, handshake.Token).ConfigureAwait(false)
                    ?? throw new IOException("Missing presentation hello");
                var hello = AgtermPresentationProtocol.Hello(AgtermPresentationProtocol.Decode(Encoding.UTF8.GetBytes(helloLine)));
                var lease = host.OpenPresentation(session, args, account, hello);
                using var subscription = lease.Subscription;
                await AgtermPresentationStream.ServeAsync(subscription, reader, writer, pipe.Dispose, lease.IsCurrent, ct, lease.Receive).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or JsonException
                or KeyNotFoundException or OperationCanceledException or TimeoutException)
            { PresentationStreamFailure?.Invoke(ex); /* Do not mix control response JSON with wire frames. */ }
            return true;
        }
    }
}
