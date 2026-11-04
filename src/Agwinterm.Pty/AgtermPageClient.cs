using System.Text;
using System.Threading.Channels;

namespace Agwinterm.Pty;

/// <summary>Bounded page request multiplexer. Cancellation keeps its permit until the peer's terminal frame.</summary>
public sealed class AgtermPageClient : IAsyncDisposable
{
    private sealed class Request(bool bridge, SemaphoreSlim permits)
    {
        public readonly bool Bridge = bridge;
        public readonly SemaphoreSlim Permits = permits;
        public readonly Channel<AgtermPageFrame> Frames = Channel.CreateBounded<AgtermPageFrame>(2);
        public readonly CancellationTokenSource Discard = new();
        public bool Terminal, Consumed;
    }
    private readonly object _gate = new();
    private readonly Dictionary<long, Request> _requests = [];
    private readonly SemaphoreSlim _resources = new(8), _bridges = new(8);
    private readonly Channel<AgtermPageFrame> _outgoing = Channel.CreateBounded<AgtermPageFrame>(32);
    private readonly CancellationTokenSource _stopped = new();
    private readonly Action _abort;
    private long _next;
    public Task Completion { get; }

    public AgtermPageClient(TextReader reader, StreamWriter writer, Action abort)
    {
        _abort = abort;
        Completion = Run(reader, writer);
    }

    public Task<string> FetchToAsync(string path, Stream destination, CancellationToken ct) =>
        RequestAsync(false, path, destination, ct);
    public Task<string> BridgeAsync(string json, CancellationToken ct) => RequestAsync(true, json, null, ct);

    private async Task<string> RequestAsync(bool bridge, string text, Stream? destination, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopped.Token);
        var permits = bridge ? _bridges : _resources;
        await permits.WaitAsync(linked.Token).ConfigureAwait(false);
        Request request;
        long id;
        lock (_gate)
        {
            if (_stopped.IsCancellationRequested) { permits.Release(); throw new IOException("Page channel closed"); }
            id = checked(++_next);
            request = new(bridge, permits);
            _requests.Add(id, request);
        }
        bool sent = false;
        try
        {
            Send(new(bridge ? "bridge" : "resource", id, text)); sent = true;
            string? mime = null;
            await foreach (var frame in request.Frames.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
            {
                switch (frame.Kind)
                {
                    case "failure": throw new IOException("Origin refused page request");
                    case "reply" when bridge: return frame.Text!;
                    case "head" when !bridge && mime is null: mime = frame.Text; break;
                    case "chunk" when !bridge && mime is not null:
                        await destination!.WriteAsync(frame.Data!, linked.Token).ConfigureAwait(false); break;
                    case "end" when !bridge && mime is not null: return mime;
                    default: Close(); throw new IOException("Invalid page response ordering");
                }
            }
            throw new IOException("Page response ended without a terminal frame");
        }
        finally
        {
            lock (_gate)
            {
                request.Consumed = true;
                request.Discard.Cancel();
                if (!sent) request.Terminal = true;
                if (!request.Terminal && !_stopped.IsCancellationRequested) Send(new("cancel", id));
                Release(id, request);
            }
        }
    }

    private void Release(long id, Request request)
    {
        if (request.Consumed && request.Terminal && _requests.Remove(id))
        { request.Permits.Release(); request.Discard.Dispose(); }
    }

    private void Send(AgtermPageFrame frame)
    {
        _ = AgtermPageProtocol.Encode(frame);
        if (_stopped.IsCancellationRequested || !_outgoing.Writer.TryWrite(frame))
        { Close(); throw new IOException("Page writer unavailable"); }
    }

    private async Task Run(TextReader reader, StreamWriter writer)
    {
        async Task Read()
        {
            while (await AgtermPresentationStream.ReadLineAsync(reader, _stopped.Token).ConfigureAwait(false) is { } line)
            {
                var frame = AgtermPageProtocol.Decode(Encoding.UTF8.GetBytes(line));
                Request request;
                CancellationToken discard;
                bool terminal = frame.Kind is "end" or "failure" or "reply";
                lock (_gate)
                {
                    if (!_requests.TryGetValue(frame.Id, out request!) || request.Terminal) throw new IOException("Unknown page response ID");
                    if (request.Bridge ? frame.Kind is not ("reply" or "failure") : frame.Kind is not ("head" or "chunk" or "end" or "failure"))
                        throw new IOException("Unexpected page response direction");
                    discard = request.Discard.Token;
                }
                try { await request.Frames.Writer.WriteAsync(frame, discard).ConfigureAwait(false); }
                catch (OperationCanceledException) when (discard.IsCancellationRequested) { }
                if (terminal)
                {
                    lock (_gate)
                    {
                        request.Terminal = true;
                        request.Frames.Writer.TryComplete();
                        Release(frame.Id, request);
                    }
                }
            }
            throw new IOException("Page channel ended");
        }
        async Task Write()
        {
            await foreach (var frame in _outgoing.Reader.ReadAllAsync(_stopped.Token).ConfigureAwait(false))
            {
                Task write = writer.WriteAsync(Encoding.UTF8.GetString(AgtermPageProtocol.Encode(frame)).AsMemory(), CancellationToken.None);
                try { await write.WaitAsync(TimeSpan.FromSeconds(5), _stopped.Token).ConfigureAwait(false); }
                catch { _abort(); try { await write.ConfigureAwait(false); } catch { } throw; }
            }
        }
        var tasks = new[] { Read(), Write() };
        try { await await Task.WhenAny(tasks).ConfigureAwait(false); }
        finally
        {
            Close();
            lock (_gate)
            {
                foreach (var (id, request) in _requests.ToArray())
                {
                    request.Terminal = true;
                    request.Discard.Cancel();
                    request.Frames.Writer.TryComplete(new IOException("Page channel closed"));
                    Release(id, request);
                }
            }
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
        }
    }

    public void Close()
    {
        _stopped.Cancel();
        _outgoing.Writer.TryComplete();
        _abort();
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        try { await Completion.ConfigureAwait(false); } catch { /* Completion exposes transport faults; disposal still drains them. */ }
    }
}
