using System.IO.Pipes;
using System.Text;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class AgtermPageServerTests
{
    private sealed class Peer : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _origin;
        private readonly NamedPipeClientStream _viewer;
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly StreamReader _originReader;
        private readonly StreamWriter _originWriter;
        public Task Serving { get; }
        public Peer(Func<string, CancellationToken, Task<AgtermPageResource>> resource,
            Func<string, CancellationToken, Task<string>>? bridge = null)
        {
            string name = "agwinterm-page-test-" + Guid.NewGuid().ToString("N");
            _origin = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _viewer = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            Task accept = _origin.WaitForConnectionAsync(_stop.Token);
            _viewer.Connect(5000); accept.GetAwaiter().GetResult();
            _reader = new(_viewer, Encoding.UTF8, false, 1024, true);
            _writer = new(_viewer, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            _originReader = new(_origin, Encoding.UTF8, false, 1024, true);
            _originWriter = new(_origin, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            Serving = AgtermPageServer.ServeAsync(_originReader, _originWriter, _origin.Dispose, resource,
                bridge ?? ((_, _) => Task.FromResult("{}")), _stop.Token);
        }
        public Task Send(AgtermPageFrame frame) => _writer.WriteAsync(Encoding.UTF8.GetString(AgtermPageProtocol.Encode(frame)));
        public async Task<AgtermPageFrame> Read()
        {
            string? line = await _reader.ReadLineAsync(_stop.Token);
            Assert.NotNull(line);
            return AgtermPageProtocol.Decode(Encoding.UTF8.GetBytes(line));
        }
        public void Disconnect() => _viewer.Dispose();
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _viewer.Dispose(); _origin.Dispose();
            try { await Serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch (IOException) { } catch (OperationCanceledException) { }
            _reader.Dispose(); _originReader.Dispose();
            foreach (var writer in new[] { _writer, _originWriter })
                try { writer.Dispose(); } catch (ObjectDisposedException) { } catch (IOException) { }
            _stop.Dispose();
        }
    }

    [Fact]
    public async Task ResourceIsChunkedWithOneTerminalAndBridgeRemainsIndependent()
    {
        byte[] bytes = Enumerable.Range(0, 150000).Select(i => (byte)i).ToArray();
        using var body = new MemoryStream(bytes);
        await using var peer = new Peer((path, _) =>
        {
            Assert.Equal("/origin/папка/index.html", path);
            return Task.FromResult(new AgtermPageResource(body, "text/html"));
        });
        await peer.Send(new("resource", 1, "/origin/папка/index.html"));
        Assert.Equal(new AgtermPageFrame("head", 1, "text/html"), await peer.Read());
        using var received = new MemoryStream();
        AgtermPageFrame frame;
        while ((frame = await peer.Read()).Kind == "chunk")
        {
            Assert.InRange(frame.Data!.Length, 1, 65536);
            received.Write(frame.Data);
        }
        Assert.Equal("end", frame.Kind); Assert.Equal(bytes, received.ToArray());
        await peer.Send(new("bridge", 2, "{}"));
        Assert.Equal(new AgtermPageFrame("reply", 2, "{}"), await peer.Read());
    }

    [Fact]
    public async Task ResourceAdmissionIsBoundedAndCancellationDoesNotBlockReader()
    {
        int active = 0;
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = new Peer(async (_, ct) =>
        {
            if (Interlocked.Increment(ref active) == 8) admitted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { Interlocked.Decrement(ref active); }
            throw new InvalidOperationException();
        });
        for (int i = 1; i <= 8; i++) await peer.Send(new("resource", i, "/file"));
        await admitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await peer.Send(new("resource", 9, "/file"));
        Assert.Equal(new AgtermPageFrame("failure", 9, "too many requests in flight"), await peer.Read());
        await peer.Send(new("bridge", 10, "{}"));
        Assert.Equal("reply", (await peer.Read()).Kind);
        await peer.Send(new("cancel", 1));
        Assert.Equal(new AgtermPageFrame("failure", 1, "cancelled"), await peer.Read());
        await peer.Send(new("cancel", 1));
        await peer.Send(new("bridge", 11, "{}"));
        Assert.Equal(new AgtermPageFrame("reply", 11, "{}"), await peer.Read());
        peer.Disconnect();
        try { await peer.Serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch (IOException) { }
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task BridgeAdmissionIsBoundedAndChannelLossCancelsEveryWaiter()
    {
        int active = 0;
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = new Peer((_, _) => Task.FromResult(new AgtermPageResource(new MemoryStream(), "text/plain")),
            async (_, ct) =>
            {
                if (Interlocked.Increment(ref active) == 8) admitted.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); return "{}"; }
                finally { Interlocked.Decrement(ref active); }
            });
        for (int i = 1; i <= 8; i++) await peer.Send(new("bridge", i, "{}"));
        await admitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await peer.Send(new("bridge", 9, "{}"));
        Assert.Equal("failure", (await peer.Read()).Kind);
        // A server-to-viewer frame is never accepted as a request.
        await peer.Send(new("reply", 10, "{}"));
        await Assert.ThrowsAsync<IOException>(() => peer.Serving.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, active);
    }
}
