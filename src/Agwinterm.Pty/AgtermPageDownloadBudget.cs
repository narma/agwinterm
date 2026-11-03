namespace Agwinterm.Pty;

/// <summary>Bounds downloaded files, including bodies retained by the browser after the network request ends.</summary>
public sealed class AgtermPageDownloadBudget(
    long resourceBytes = 64 * 1024 * 1024,
    long pageBytes = 256 * 1024 * 1024,
    int resources = 64)
{
    private readonly object _gate = new();
    private readonly long _resourceBytes = resourceBytes > 0 ? resourceBytes : throw new ArgumentOutOfRangeException(nameof(resourceBytes));
    private readonly long _pageBytes = pageBytes > 0 ? pageBytes : throw new ArgumentOutOfRangeException(nameof(pageBytes));
    private readonly int _resources = resources > 0 ? resources : throw new ArgumentOutOfRangeException(nameof(resources));
    private long _used;
    private int _open;

    public Stream Create(Func<Stream> create)
    {
        lock (_gate)
        {
            if (_open == _resources) throw new IOException("Remote page has too many retained resources");
            _open++;
        }
        try { return new Resource(this, create()); }
        catch { lock (_gate) _open--; throw; }
    }

    private sealed class Resource(AgtermPageDownloadBudget budget, Stream content) : Stream
    {
        private Stream? _content = content;
        private long _reserved;
        private Stream Content => Volatile.Read(ref _content) ?? throw new ObjectDisposedException(nameof(Resource));
        private Stream Reserve(int count)
        {
            lock (budget._gate)
            {
                var stream = Content;
                if (count > budget._resourceBytes - _reserved || count > budget._pageBytes - budget._used)
                    throw new IOException("Remote page resource exceeds its download storage budget");
                _reserved += count;
                budget._used += count;
                return stream;
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) => Reserve(buffer.Length).Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            Reserve(buffer.Length).WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Stream? owned;
                lock (budget._gate) { owned = _content; _content = null; }
                if (owned is not null)
                {
                    // Keep the reservation until the file is actually closed/deleted.
                    try { owned.Dispose(); }
                    finally { lock (budget._gate) { budget._used -= _reserved; budget._open--; } }
                }
            }
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => Content.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => Content.Read(buffer);
        public override bool CanRead => Volatile.Read(ref _content)?.CanRead ?? false;
        public override bool CanSeek => Volatile.Read(ref _content)?.CanSeek ?? false;
        public override bool CanWrite => Volatile.Read(ref _content)?.CanWrite ?? false;
        public override long Length => Content.Length;
        public override long Position { get => Content.Position; set => Content.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => Content.Seek(offset, origin);
        public override void Flush() => Content.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
