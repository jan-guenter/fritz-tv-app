using System.Runtime.InteropServices;
using FritzTV.Core.Models;
using FritzTV.Infrastructure.Dvb;

namespace FritzTV.Infrastructure.Timeshift;

public sealed class IndexedTimeshiftBuffer : IAsyncDisposable
{
    private readonly string directory;
    private readonly TimeSpan retention;
    private readonly long byteLimit;
    private readonly long freeSpaceReserve;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim writerGate = new(1, 1);
    private readonly Lock stateLock = new();
    private readonly List<BufferSegment> segments = [];
    private readonly Dictionary<int, byte[]> programmeTables = [];
    private FileStream? writer;
    private long nextSegmentId;
    private long totalBytes;
    private long lastFlush;
    private bool completed;
    private int? videoPid;
    private int? pmtPid;

    public IndexedTimeshiftBuffer(string directory, TimeSpan? retention = null, long byteLimit = 10L * 1024 * 1024 * 1024, long freeSpaceReserve = 1024L * 1024 * 1024, TimeProvider? timeProvider = null)
    {
        this.directory = Path.GetFullPath(directory);
        this.retention = retention ?? TimeSpan.FromHours(1);
        ArgumentOutOfRangeException.ThrowIfLessThan(byteLimit, 188);
        ArgumentOutOfRangeException.ThrowIfNegative(freeSpaceReserve);
        if (this.retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention));
        }

        this.byteLimit = byteLimit;
        this.freeSpaceReserve = freeSpaceReserve;
        clock = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(this.directory);
        if (Directory.EnumerateFileSystemEntries(this.directory).Any())
        {
            throw new IOException("Use a new per-session directory for the timeshift buffer.");
        }
    }

    public TimeshiftRange? Range
    {
        get
        {
            lock (stateLock)
            {
                return segments.Count == 0 ? null : new TimeshiftRange(segments[0].Start, segments[^1].End, totalBytes);
            }
        }
    }

    public void SetProgramme(ProgrammeMap programme)
    {
        ArgumentNullException.ThrowIfNull(programme);
        lock (stateLock)
        {
            pmtPid = programme.PmtPid;
            videoPid = programme.Streams.FirstOrDefault(item => item.StreamType is 0x01 or 0x02 or 0x10 or 0x1b or 0x24)?.Pid;
        }
    }

    public async Task AppendAsync(ReadOnlyMemory<byte> transport, CancellationToken cancellationToken = default)
    {
        if (transport.Length % 188 != 0)
        {
            throw new InvalidDataException("Timeshift input must contain complete 188-byte transport packets.");
        }

        await writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(completed, this);
            var now = clock.GetUtcNow();
            if (writer is null)
            {
                CheckFreeSpace();
                StartSegment(now);
            }

            await AppendPacketsAsync(transport, now, cancellationToken).ConfigureAwait(false);
            if (clock.GetElapsedTime(lastFlush) >= TimeSpan.FromMilliseconds(100))
            {
                await writer!.FlushAsync(cancellationToken).ConfigureAwait(false);
                lastFlush = clock.GetTimestamp();
            }

            Evict(now);
        }
        finally
        {
            writerGate.Release();
        }
    }

    public TimeshiftReadStream OpenReader(DateTimeOffset? requested = null, CancellationToken cancellationToken = default)
    {
        lock (stateLock)
        {
            if (segments.Count == 0)
            {
                throw new InvalidOperationException("The timeshift buffer has no transport data yet.");
            }

            var at = requested ?? segments[^1].End;
            var candidates = segments.SelectMany(segment => segment.Entries.Select(entry => (Segment: segment, Entry: entry))).ToArray();
            var selected = candidates.LastOrDefault(item => item.Entry.At <= at);
            if (selected.Segment is null)
            {
                selected = candidates.FirstOrDefault();
            }

            var segment = selected.Segment ?? segments[0];
            var offset = selected.Segment is null ? 0 : selected.Entry.Offset;
            var actual = selected.Segment is null ? segment.Start : selected.Entry.At;
            return new TimeshiftReadStream(this, segment.Id, offset, actual, programmeTables.Values.SelectMany(item => item).ToArray(), cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await writerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            completed = true;
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
                writer = null;
            }
        }
        finally
        {
            writerGate.Release();
        }
    }

    internal async ValueTask<(int Count, long SegmentId, long Offset)> ReadAsync(long segmentId, long offset, Memory<byte> output, CancellationToken cancellationToken)
    {
        while (true)
        {
            var located = Locate(segmentId, offset);
            if (located.Segment is null)
            {
                return (0, segmentId, offset);
            }

            segmentId = located.Segment.Id;
            offset = located.Offset;
            try
            {
                using var handle = File.OpenHandle(located.Segment.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous | FileOptions.RandomAccess);
                var count = await RandomAccess.ReadAsync(handle, output, offset, cancellationToken).ConfigureAwait(false);
                if (count > 0)
                {
                    return (count, segmentId, offset + count);
                }
            }
            catch (FileNotFoundException)
            {
                continue;
            }

            if (completed && located.IsLast)
            {
                return (0, segmentId, offset);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(40), clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AppendPacketsAsync(ReadOnlyMemory<byte> transport, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var chunkStart = 0;
        for (var offset = 0; offset < transport.Length; offset += 188)
        {
            var packet = transport.Slice(offset, 188);
            if (!TsPacket.TryParse(packet.Span, out var ts))
            {
                continue;
            }

            var entryPoint = ts.StartsPayload && (videoPid is null ? ts.Pcr is not null : ts.Pid == videoPid && ts.RandomAccess);
            if (entryPoint && now - segments[^1].Start >= TimeSpan.FromSeconds(2))
            {
                await WriteChunkAsync(transport[chunkStart..offset], now, cancellationToken).ConfigureAwait(false);
                await writer!.DisposeAsync().ConfigureAwait(false);
                CheckFreeSpace();
                StartSegment(now);
                chunkStart = offset;
            }

            lock (stateLock)
            {
                if (entryPoint)
                {
                    segments[^1].Entries.Add(new SeekEntry(now, segments[^1].Length + offset - chunkStart));
                }

                if (ts.Pid == 0 || ts.Pid == pmtPid)
                {
                    programmeTables[ts.Pid] = packet.ToArray();
                }
            }
        }

        await WriteChunkAsync(transport[chunkStart..], now, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteChunkAsync(ReadOnlyMemory<byte> bytes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        await writer!.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        lock (stateLock)
        {
            segments[^1].Length += bytes.Length;
            segments[^1].End = now;
            totalBytes += bytes.Length;
        }
    }

    private void StartSegment(DateTimeOffset now)
    {
        var segment = new BufferSegment(++nextSegmentId, Path.Combine(directory, nextSegmentId.ToString("D12", System.Globalization.CultureInfo.InvariantCulture) + ".ts"), now);
        writer = new FileStream(segment.Path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
        lock (stateLock)
        {
            segments.Add(segment);
        }
    }

    private void Evict(DateTimeOffset now)
    {
        lock (stateLock)
        {
            while (segments.Count > 1 && (now - segments[0].Start > retention || totalBytes > byteLimit))
            {
                var expired = segments[0];
                File.Delete(expired.Path);
                totalBytes -= expired.Length;
                segments.RemoveAt(0);
            }

            if (totalBytes > byteLimit)
            {
                throw new IOException("The timeshift byte limit was reached before a new decodable segment could be started.");
            }
        }
    }

    private (BufferSegment? Segment, long Offset, bool IsLast) Locate(long segmentId, long offset)
    {
        lock (stateLock)
        {
            var index = segments.FindIndex(segment => segment.Id >= segmentId);
            if (index < 0)
            {
                return (null, 0, true);
            }

            if (segments[index].Id != segmentId)
            {
                offset = segments[index].Entries.Count > 0 ? segments[index].Entries[0].Offset : 0;
            }

            if (offset >= segments[index].Length && index < segments.Count - 1)
            {
                return (segments[index + 1], 0, index + 1 == segments.Count - 1);
            }

            return (segments[index], offset, index == segments.Count - 1);
        }
    }

    private void CheckFreeSpace()
    {
        if (new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace < freeSpaceReserve)
        {
            throw new IOException("The timeshift free-space reserve has been reached.");
        }
    }

    private sealed class BufferSegment(long id, string path, DateTimeOffset start)
    {
        public long Id { get; } = id;

        public string Path { get; } = path;

        public DateTimeOffset Start { get; } = start;

        public DateTimeOffset End { get; set; } = start;

        public long Length { get; set; }

        public List<SeekEntry> Entries { get; } = [];
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct SeekEntry(DateTimeOffset At, long Offset);
}
