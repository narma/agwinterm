using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agwinterm.Pty;

/// <summary>Own the saved window library before any session can be restored.</summary>
public sealed class GuiInstance : IDisposable
{
    public sealed record Launch(string? Directory = null, string? Profile = null,
        bool NewWindow = false, bool Maximized = false, bool Fullscreen = false);
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cancel = new();
    private Task _listener = Task.CompletedTask;
    public bool IsOwner { get; }

    public GuiInstance(string libraryDirectory)
    {
        // The library, not --pipe, is the resource being protected. Two control
        // pipe names must not permit restoring the same pane IDs twice.
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(libraryDirectory).TrimEnd('\\', '/').ToUpperInvariant())));
        _pipeName = "agwinterm-gui-" + key;
        // AppDir and the host pipes are shared across Windows logon sessions.
        _mutex = new Mutex(false, _pipeName, new NamedWaitHandleOptions
        { CurrentUserOnly = true, CurrentSessionOnly = false });
        try { IsOwner = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsOwner = true; }
    }

    public void Listen(Action<Launch> launch)
    {
        if (!IsOwner) throw new InvalidOperationException("Only the library owner may listen.");
        _listener = Task.Run(async () =>
        {
            while (!_cancel.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_cancel.Token).ConfigureAwait(false);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    var size = new byte[4];
                    await pipe.ReadExactlyAsync(size, timeout.Token).ConfigureAwait(false);
                    int length = BitConverter.ToInt32(size);
                    if (length is < 1 or > 65536) continue;
                    var bytes = new byte[length];
                    await pipe.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
                    var request = JsonSerializer.Deserialize<Launch>(bytes);
                    if (request is null) continue;
                    // The UI hop has its own 15-second deadline. Reading the
                    // message must not consume the time allowed for its reply.
                    timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    launch(request);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await pipe.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidOperationException or TimeoutException)
                { System.Diagnostics.Trace.WriteLine("GUI launch forwarding: " + ex.Message); }
            }
        });
    }

    public async Task ForwardAsync(Launch launch, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(launch);
        if (bytes.Length > 65536) throw new ArgumentException("Launch arguments are too large.");
        await pipe.WriteAsync(BitConverter.GetBytes(bytes.Length), timeout.Token).ConfigureAwait(false);
        await pipe.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
        // Allow the owner's complete UI budget plus time to return its ack.
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var ack = new byte[1];
        await pipe.ReadExactlyAsync(ack, timeout.Token).ConfigureAwait(false);
        if (ack[0] != 1) throw new IOException("Launch was not accepted.");
    }

    // Construct and dispose on the same thread: it owns the named mutex.
    public void Dispose()
    {
        _cancel.Cancel();
        _listener.GetAwaiter().GetResult();
        _cancel.Dispose();
        if (IsOwner) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
