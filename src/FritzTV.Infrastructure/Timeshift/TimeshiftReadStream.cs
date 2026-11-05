namespace FritzTV.Infrastructure.Timeshift;

public sealed class TimeshiftReadStream : Stream
{
    private readonly IndexedTimeshiftBuffer owner;
    private readonly CancellationTokenSource lifetime;
    private readonly byte[] prefix;
    private int prefixOffset;
    private long segmentId;
    private long offset;

    internal TimeshiftReadStream(IndexedTimeshiftBuffer owner, long segmentId, long offset, DateTimeOffset actualStart, byte[] prefix, CancellationToken cancellationToken)
    {
        this.owner = owner;
        this.segmentId = segmentId;
        this.offset = offset;
        this.prefix = prefix;
        ActualStart = actualStart;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    public DateTimeOffset ActualStart { get; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        using var combined = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        if (prefixOffset < prefix.Length)
        {
            var count = Math.Min(prefix.Length - prefixOffset, buffer.Length);
            prefix.AsMemory(prefixOffset, count).CopyTo(buffer);
            prefixOffset += count;
            return count;
        }

        var result = await owner.ReadAsync(segmentId, offset, buffer, combined.Token).ConfigureAwait(false);
        segmentId = result.SegmentId;
        offset = result.Offset;
        return result.Count;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lifetime.Cancel();
            lifetime.Dispose();
        }

        base.Dispose(disposing);
    }
}
