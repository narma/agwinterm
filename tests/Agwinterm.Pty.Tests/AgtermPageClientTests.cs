using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;

namespace Agwinterm.Pty.Tests;

public class AgtermPageClientTests
{
    private sealed class Wire : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _origin;
        private readonly NamedPipeClientStream _viewer;
        private readonly StreamReader _originReader, _viewerReader;
        private readonly StreamWriter _originWriter, _viewerWriter;
        private readonly Channel<AgtermPageFrame> _frames = Channel.CreateUnbounded<AgtermPageFrame>();
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
        private readonly Task _reading;
        public AgtermPageClient Client { get; }
        public Wire()
        {
            string name = "aw-page-client-" + Guid.NewGuid().ToString("N");
            _origin = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _viewer = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            var accept = _origin.WaitForConnectionAsync(_stop.Token);
            _viewer.Connect(5000); accept.GetAwaiter().GetResult();
            _originReader = new(_origin, Encoding.UTF8, false, 1024, true);
            _viewerReader = new(_viewer, Encoding.UTF8, false, 1024, true);
            _originWriter = new(_origin, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            _viewerWriter = new(_viewer, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            Client = new(_viewerReader, _viewerWriter, _viewer.Dispose);
            _reading = Read();
        }
        private async Task Read()
        {
            try
            {
                while (await _originReader.ReadLineAsync(_stop.Token) is { } line)
                    await _frames.Writer.WriteAsync(AgtermPageProtocol.Decode(Encoding.UTF8.GetBytes(line)));
            }
            finally { _frames.Writer.TryComplete(); }
        }
        public ValueTask<AgtermPageFrame> Take() => _frames.Reader.ReadAsync(_stop.Token);
        public bool HasFrame => _frames.Reader.TryPeek(out _);
        public Task Send(AgtermPageFrame frame) => _originWriter.WriteAsync(Encoding.UTF8.GetString(AgtermPageProtocol.Encode(frame)));
        public void Disconnect() => _origin.Dispose();
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync(); _stop.Cancel(); _origin.Dispose();
            try { await _reading; } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            _originReader.Dispose(); _viewerReader.Dispose();
            foreach (var writer in new[] { _originWriter, _viewerWriter })
                try { writer.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            _stop.Dispose();
        }
    }

    [Fact]
    public async Task ResourceChunksAndBridgeRepliesHaveIndependentRequests()
    {
        await using var wire = new Wire();
        using var body = new MemoryStream();
        var fetch = wire.Client.FetchToAsync("/origin/file", body, CancellationToken.None);
        var resource = await wire.Take(); Assert.Equal("resource", resource.Kind);
        var bridge = wire.Client.BridgeAsync("{}", CancellationToken.None);
        var request = await wire.Take(); Assert.Equal("bridge", request.Kind);
        await wire.Send(new("head", resource.Id, "text/plain"));
        await wire.Send(new("chunk", resource.Id, Data: "hello"u8.ToArray()));
        await wire.Send(new("reply", request.Id, "{\"ok\":true}"));
        Assert.Equal("{\"ok\":true}", await bridge.WaitAsync(TimeSpan.FromSeconds(5)));
        await wire.Send(new("end", resource.Id));
        Assert.Equal("text/plain", await fetch.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("hello", Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task CancellationKeepsPermitUntilTerminalThenChannelLossSettlesAllWaiters()
    {
        await using var wire = new Wire();
        using var first = new CancellationTokenSource();
        var requests = Enumerable.Range(0, 8).Select(i => wire.Client.FetchToAsync("/file", Stream.Null,
            i == 0 ? first.Token : CancellationToken.None)).ToArray();
        var frames = new List<AgtermPageFrame>();
        for (int i = 0; i < 8; i++) frames.Add(await wire.Take());
        var ninth = wire.Client.FetchToAsync("/next", Stream.Null, CancellationToken.None);
        first.Cancel();
        var cancel = await wire.Take(); Assert.Equal("cancel", cancel.Kind); Assert.Equal(frames[0].Id, cancel.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requests[0]);
        Assert.False(wire.HasFrame); Assert.False(ninth.IsCompleted);
        await wire.Send(new("failure", cancel.Id, "cancelled"));
        var next = await wire.Take(); Assert.Equal("/next", next.Text);
        wire.Disconnect();
        foreach (var request in requests.Skip(1).Append(ninth))
            await Assert.ThrowsAnyAsync<Exception>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task StorageLimitCancelsAnUnfinishedResourceWithoutClosingTheChannel()
    {
        await using var wire = new Wire();
        var budget = new AgtermPageDownloadBudget(resourceBytes: 4, pageBytes: 4);
        using var body = budget.Create(() => new MemoryStream());
        var fetch = wire.Client.FetchToAsync("/unending", body, CancellationToken.None);
        var request = await wire.Take();
        await wire.Send(new("head", request.Id, "application/octet-stream"));
        await wire.Send(new("chunk", request.Id, Data: new byte[4]));
        await wire.Send(new("chunk", request.Id, Data: new byte[1]));
        await Assert.ThrowsAsync<IOException>(() => fetch.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(4, body.Length);
        var cancel = await wire.Take();
        Assert.Equal("cancel", cancel.Kind); Assert.Equal(request.Id, cancel.Id);
        // A peer can keep sending after cancellation; discarded bytes never reach storage.
        await wire.Send(new("chunk", request.Id, Data: new byte[4]));
        await wire.Send(new("failure", request.Id, "cancelled"));
        var bridge = wire.Client.BridgeAsync("{}", CancellationToken.None);
        var next = await wire.Take();
        await wire.Send(new("reply", next.Id, "ok"));
        Assert.Equal("ok", await bridge.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(4, body.Length);
    }

    [Fact]
    public async Task WrongDirectionClosesChannelAndSettlesBridge()
    {
        await using var wire = new Wire();
        var bridge = wire.Client.BridgeAsync("{}", CancellationToken.None);
        var request = await wire.Take();
        await wire.Send(new("resource", request.Id, "/unexpected"));
        await Assert.ThrowsAsync<IOException>(() => wire.Client.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<Exception>(() => bridge.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
