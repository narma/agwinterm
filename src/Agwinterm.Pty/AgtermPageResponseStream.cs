namespace Agwinterm.Pty;

/// <summary>A sequential browser response releases its temporary file as soon as its body is consumed.</summary>
public sealed class AgtermPageResponseStream(Stream content, Action released) : Stream
{
    private readonly object _gate = new();
    private Stream? _content = content;
    private readonly long _length = content.Length;
    private long _position;
    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            if (_content is null || count == 0) return 0;
            int read = _content.Read(buffer, offset, count);
            _position += read;
            if (read == 0 || _position == _length) Release();
            return read;
        }
    }
    private void Release()
    {
        if (_content is not { } owned) return;
        _content = null;
        try { owned.Dispose(); } finally { released(); }
    }
    protected override void Dispose(bool disposing)
    { if (disposing) lock (_gate) Release(); base.Dispose(disposing); }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
