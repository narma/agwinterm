using System.Diagnostics;
using System.IO.Pipes;

namespace Agwinterm.Pty.Tests;

public sealed class PtyHostClientTimeoutTests
{
    [Fact]
    public async Task ConnectedPeerThatNeverAnswersHelloCannotHangStartup()
    {
        string id = "agwinterm-silent-peer-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(PtyHostServer.ControlPipeName(id),
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepting = server.WaitForConnectionAsync();
        var timer = Stopwatch.StartNew();
        var connecting = Task.Run(() => Assert.Throws<IOException>(() => PtyHostClient.Connect(id, 200)));
        await accepting.WaitAsync(TimeSpan.FromSeconds(5));
        var error = await connecting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("must not be replayed", error.Message);
        Assert.IsType<OperationCanceledException>(error.InnerException);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimedOutExchangeFinishesIoBeforeDisposalAndCannotBeReused(bool blockWrite)
    {
        using var stream = new StalledStream(blockWrite);
        using var client = new PtyHostClient(stream, requestTimeoutMs: 100);
        var error = await Task.Run(() => Assert.Throws<IOException>(() => client.List()))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("timed out", error.Message);
        Assert.False(client.IsUsable);
        Assert.True(stream.Disposed);
        Assert.False(stream.DisposedInFlight);
        int writes = stream.Writes;
        Assert.Throws<IOException>(() => client.List());
        Assert.Equal(writes, stream.Writes);
    }

    private sealed class StalledStream(bool blockWrite) : MemoryStream
    {
        private bool _inFlight;
        public bool Disposed, DisposedInFlight;
        public int Writes;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellation = default)
        {
            Writes++;
            if (blockWrite) await Stall(cancellation);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> bytes, CancellationToken cancellation = default)
        {
            await Stall(cancellation);
            return 0;
        }
        private async Task Stall(CancellationToken cancellation)
        {
            _inFlight = true;
            try { await Task.Delay(Timeout.Infinite, cancellation); }
            finally { _inFlight = false; }
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true; DisposedInFlight |= _inFlight;
            base.Dispose(disposing);
        }
    }
}
