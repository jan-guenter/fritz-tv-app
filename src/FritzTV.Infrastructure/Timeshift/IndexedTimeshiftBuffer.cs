using System.Runtime.InteropServices;
using FritzTV.Core.Models;
using FritzTV.Infrastructure.Dvb;

namespace FritzTV.Infrastructure.Timeshift;

public sealed class IndexedTimeshiftBuffer : IAsyncDisposable
{
    private readonly string directory;
    private readonly FileStream ownerLease;
    private readonly TimeSpan retention;
    private readonly long byteLimit;
    private readonly long freeSpaceReserve;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim writerGate = new(1, 1);
    private readonly Lock stateLock = new();
    private readonly List<BufferSegment> segments = [];
    private readonly Dictionary<int, PsiTableSnapshot> programmeTables = [];
    private readonly Dictionary<Guid, long> pins = [];
    private readonly PsiSectionAssembler tableAssembler = new();
    private readonly ProgrammeClock programmeClock = new();
    private int? pcrPid;
    private DateTimeOffset? programmePosition;
    private FileStream? writer;
    private long nextSegmentId;
    private long totalBytes;
    private long lastFlush;
    private bool completed;
    private bool recordingOnly;
    private int? videoPid;
    private int? audioPid;
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
        tableAssembler.SectionReceived += OnTableSectionReceived;
        Directory.CreateDirectory(this.directory);
        if (Directory.EnumerateFileSystemEntries(this.directory).Any())
        {
            throw new IOException("Use a new per-session directory for the timeshift buffer.");
        }

        ownerLease = TimeshiftBufferMaintenance.CreateOwnership(this.directory);
    }

    public TimeshiftRange? Range
    {
        get
        {
            lock (stateLock)
            {
                if (segments.Count == 0)
                {
                    return null;
                }

                var visible = VisibleSegments();
                var entries = visible.SelectMany(segment => segment.Entries).ToArray();
                var earliest = entries.Length > 0 ? entries[videoPid is not null && entries.Length > 1 ? 1 : 0].At : visible[0].Start;
                return new TimeshiftRange(earliest, segments[^1].End, totalBytes);
            }
        }
    }

    public bool CanStartPlayback
    {
        get
        {
            lock (stateLock)
            {
                return videoPid is null ? audioPid is not null && segments.Any(segment => segment.Entries.Count > 0) : segments.Sum(segment => segment.Entries.Count) >= 2;
            }
        }
    }

    public void SetProgramme(ProgrammeMap programme)
    {
        ArgumentNullException.ThrowIfNull(programme);
        lock (stateLock)
        {
            pmtPid = programme.PmtPid;
            pcrPid = programme.PcrPid;
            foreach (var oldPid in programmeTables.Keys.Where(pid => pid != 0 && pid != pmtPid).ToArray())
            {
                programmeTables.Remove(oldPid);
            }
            videoPid = programme.Streams.FirstOrDefault(item => item.StreamType is 0x01 or 0x02 or 0x10 or 0x1b or 0x24)?.Pid;
            audioPid = programme.Streams.FirstOrDefault(item => (item.StreamType is 0x03 or 0x04 or 0x0f or 0x11 or 0x06 or 0x81 or 0x87) && !item.IsTeletext && !item.IsDvbSubtitle)?.Pid;
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

    public TimeshiftReadStream OpenReader(DateTimeOffset? requested = null, CancellationToken cancellationToken = default) => OpenReaderCore(requested, false, cancellationToken);

    public TimeshiftReadStream OpenPinnedReader(DateTimeOffset? requested = null, CancellationToken cancellationToken = default) => OpenReaderCore(requested, true, cancellationToken);

    public void ReleaseViewingRetention()
    {
        lock (stateLock)
        {
            recordingOnly = true;
        }
    }

    internal void Unpin(Guid id)
    {
        lock (stateLock)
        {
            pins.Remove(id);
        }
    }

    private TimeshiftReadStream OpenReaderCore(DateTimeOffset? requested, bool pin, CancellationToken cancellationToken)
    {
        lock (stateLock)
        {
            if (segments.Count == 0)
            {
                throw new InvalidOperationException("The timeshift buffer has no transport data yet.");
            }

            var available = pin ? segments.ToArray() : VisibleSegments();
            var at = requested ?? segments[^1].End;
            var candidates = available.SelectMany(segment => segment.Entries.Select(entry => (Segment: segment, Entry: entry))).ToArray();
            var targetIndex = Array.FindLastIndex(candidates, item => item.Entry.At <= at);
            var minimumTarget = !pin && videoPid is not null && candidates.Length > 1 ? 1 : 0;
            targetIndex = Math.Max(minimumTarget, targetIndex);
            var inputIndex = !pin && videoPid is not null ? Math.Max(0, targetIndex - 1) : targetIndex;
            var selected = candidates.Length > 0 ? candidates[inputIndex] : default;
            var segment = selected.Segment ?? available[0];
            var offset = selected.Segment is null ? 0 : selected.Entry.Offset;
            var actual = selected.Segment is null ? segment.Start : selected.Entry.At;
            var targetAt = candidates.Length > 0 ? candidates[targetIndex].Entry.At : actual;
            Guid? pinId = pin ? Guid.NewGuid() : null;
            if (pinId is { } identifier)
            {
                pins.Add(identifier, segment.Id);
            }

            return new TimeshiftReadStream(this, segment.Id, offset, actual, GetProgrammeTables(), pinId, targetAt, cancellationToken);
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
            await ownerLease.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            writerGate.Release();
        }
    }

    internal async ValueTask<(int Count, long SegmentId, long Offset)> ReadAsync(long segmentId, long offset, Memory<byte> output, Guid? pinId, CancellationToken cancellationToken)
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
                    if (pinId is { } identifier)
                    {
                        lock (stateLock)
                        {
                            pins[identifier] = segmentId;
                        }
                    }

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

            if (ts.Pid == pcrPid && ts.Pcr is { } pcr)
            {
                programmePosition = programmeClock.Update(pcr, now, ts.Discontinuity);
            }

            var entryTime = programmePosition ?? now;
            var entryPoint = ts.StartsPayload && (videoPid is null ? ts.Pid == audioPid : ts.Pid == videoPid && ts.RandomAccess);
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
                    segments[^1].Entries.Add(new SeekEntry(entryTime, segments[^1].Length + offset - chunkStart));
                }

                if (ts.Pid == 0 || ts.Pid == pmtPid)
                {
                    tableAssembler.Push(packet.Span);
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
            while (segments.Count > 1 && ((recordingOnly && pins.Count > 0) || now - segments[0].Start > retention || totalBytes > byteLimit))
            {
                var expired = segments[0];
                if (pins.Values.Any(id => id <= expired.Id))
                {
                    break;
                }

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

    private BufferSegment[] VisibleSegments()
    {
        var cutoff = segments[^1].End - retention;
        var visible = segments.Where(segment => segment.End >= cutoff).ToArray();
        return visible.Length > 0 ? visible : [segments[^1]];
    }

    private void OnTableSectionReceived(object? sender, PsiSectionEventArgs section)
    {
        if (section.Data.Span[0] is 0 or 2)
        {
            if (!programmeTables.TryGetValue(section.Pid, out var table))
            {
                table = new PsiTableSnapshot();
                programmeTables.Add(section.Pid, table);
            }

            table.Observe(section.Data.Span);
        }
    }

    private byte[] GetProgrammeTables()
    {
        var packets = new List<byte>();
        foreach (var (pid, table) in programmeTables.OrderBy(item => item.Key))
        {
            var continuity = 0;
            foreach (var section in table.Sections)
            {
                var transport = PsiPacketizer.Packetize(pid, section.Span, continuity);
                packets.AddRange(transport);
                continuity = (continuity + transport.Length / 188) & 15;
            }
        }

        return packets.ToArray();
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
