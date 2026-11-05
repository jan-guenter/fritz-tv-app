namespace FritzTV.Desktop.Services;

public sealed class PauseControlledReadStream(Stream inner, CancellationToken cancellationToken) : Stream
{
    private readonly ManualResetEventSlim _readAllowed = new(initialState: true);
    private int _disposed;

    public override bool CanRead => Volatile.Read(ref _disposed) == 0;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public void SetPaused(bool paused)
    {
        if (paused)
        {
            _readAllowed.Reset();
        }
        else
        {
            _readAllowed.Set();
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        _readAllowed.Wait(cancellationToken);
        return inner.Read(buffer);
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _readAllowed.Dispose();
        }

        base.Dispose(disposing);
    }
}
