using FritzTV.Infrastructure.Dvb;
using FritzTV.Infrastructure.Timeshift;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class TimeshiftTests
{
    [Fact]
    public async Task RetentionClampsExpiredPausedPointAndReaderGetsCompletePackets()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fritztv-buffer-tests-" + Guid.NewGuid().ToString("N"));
        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new ManualTimeProvider();
        var buffer = new IndexedTimeshiftBuffer(directory, TimeSpan.FromSeconds(30), byteLimit: 188, freeSpaceReserve: 0, time);
        try
        {
            buffer.SetProgramme(new ProgrammeMap(1, 99, 100, [new ElementaryStreamInfo(100, 0x1b, "", false, false)]));
            var oldPosition = time.GetUtcNow();
            await buffer.AppendAsync(Packet(1), cancellationToken);
            time.Advance(TimeSpan.FromSeconds(3));
            await buffer.AppendAsync(Packet(2), cancellationToken);
            Assert.Equal(188, buffer.Range!.Bytes);
            Assert.Equal(time.GetUtcNow(), buffer.Range.Clamp(oldPosition));
            await buffer.DisposeAsync();
            using var reader = buffer.OpenReader(oldPosition, cancellationToken);
            Assert.Equal(time.GetUtcNow(), reader.ActualStart);
            var data = new byte[188];
            Assert.Equal(188, await reader.ReadAsync(data, cancellationToken));
            Assert.Equal(2, data[^1]);
            Assert.Equal(0, await reader.ReadAsync(data, cancellationToken));
        }
        finally
        {
            await buffer.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RadioRetentionDoesNotRequirePcrOnThePesStartPacket()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fritztv-radio-buffer-tests-" + Guid.NewGuid().ToString("N"));
        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new ManualTimeProvider();
        var buffer = new IndexedTimeshiftBuffer(directory, TimeSpan.FromSeconds(1), byteLimit: 1880, freeSpaceReserve: 0, time);
        try
        {
            buffer.SetProgramme(new ProgrammeMap(1, 99, 100, [new ElementaryStreamInfo(100, 0x11, "deu", false, false)]));
            await buffer.AppendAsync(Packet(1), cancellationToken);
            time.Advance(TimeSpan.FromSeconds(3));
            await buffer.AppendAsync(Packet(2), cancellationToken);
            Assert.Equal(188, buffer.Range!.Bytes);
            Assert.Equal(time.GetUtcNow(), buffer.Range.Earliest);
        }
        finally
        {
            await buffer.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecordingPinsPreserveUnreadSegmentsWithoutExtendingViewingRetention()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fritztv-pinned-buffer-tests-" + Guid.NewGuid().ToString("N"));
        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new ManualTimeProvider();
        var buffer = new IndexedTimeshiftBuffer(directory, TimeSpan.FromSeconds(1), byteLimit: 1880, freeSpaceReserve: 0, time);
        try
        {
            buffer.SetProgramme(new ProgrammeMap(1, 99, 100, [new ElementaryStreamInfo(100, 0x11, "deu", false, false)]));
            var start = time.GetUtcNow();
            await buffer.AppendAsync(Packet(1), cancellationToken);
            using var pinned = buffer.OpenPinnedReader(start, cancellationToken);
            time.Advance(TimeSpan.FromSeconds(3));
            await buffer.AppendAsync(Packet(2), cancellationToken);
            Assert.Equal(376, buffer.Range!.Bytes);
            Assert.Equal(time.GetUtcNow(), buffer.Range.Earliest);
            var packet = new byte[188];
            Assert.Equal(188, await pinned.ReadAsync(packet, cancellationToken));
            Assert.Equal(1, packet[^1]);
        }
        finally
        {
            await buffer.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecordingOnlyBufferReleasesConsumedSegmentsAndStaysBounded()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fritztv-progressing-pin-tests-" + Guid.NewGuid().ToString("N"));
        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new ManualTimeProvider();
        var buffer = new IndexedTimeshiftBuffer(directory, TimeSpan.FromHours(1), byteLimit: 564, freeSpaceReserve: 0, time);
        try
        {
            buffer.SetProgramme(new ProgrammeMap(1, 99, 100, [new ElementaryStreamInfo(100, 0x11, "deu", false, false)]));
            time.Advance(TimeSpan.FromSeconds(1));
            await buffer.AppendAsync(Packet(0), cancellationToken);
            using var pinned = buffer.OpenPinnedReader(time.GetUtcNow(), cancellationToken);
            buffer.ReleaseViewingRetention();
            var packet = new byte[188];
            for (byte marker = 1; marker <= 20; marker++)
            {
                Assert.Equal(188, await pinned.ReadAsync(packet, cancellationToken));
                Assert.Equal(marker - 1, packet[^1]);
                time.Advance(TimeSpan.FromSeconds(3));
                await buffer.AppendAsync(Packet(marker), cancellationToken);
                Assert.InRange(buffer.Range!.Bytes, 188, 376);
            }
        }
        finally
        {
            await buffer.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExpiredVideoCursorTargetsVisibleEarliestWhileDecoderGetsHiddenPreroll()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fritztv-preroll-target-tests-" + Guid.NewGuid().ToString("N"));
        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new ManualTimeProvider();
        var buffer = new IndexedTimeshiftBuffer(directory, TimeSpan.FromSeconds(30), byteLimit: 1880, freeSpaceReserve: 0, time);
        try
        {
            buffer.SetProgramme(new ProgrammeMap(1, 99, 100, [new ElementaryStreamInfo(100, 0x1b, "", false, false)]));
            var start = time.GetUtcNow();
            await buffer.AppendAsync(Packet(1), cancellationToken);
            time.Advance(TimeSpan.FromSeconds(3));
            await buffer.AppendAsync(Packet(2), cancellationToken);
            using var reader = buffer.OpenReader(start.AddMinutes(-1), cancellationToken);
            Assert.Equal(start, reader.ActualStart);
            Assert.Equal(buffer.Range!.Earliest, reader.TargetStart);
            Assert.Equal(TimeSpan.FromSeconds(3), reader.WarmupDuration);
        }
        finally
        {
            await buffer.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DecoderPrefixContainsEveryCurrentPatSectionWithContinuousCounters()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fritztv-table-prefix-tests-" + Guid.NewGuid().ToString("N"));
        var cancellationToken = TestContext.Current.CancellationToken;
        var buffer = new IndexedTimeshiftBuffer(directory, freeSpaceReserve: 0);
        try
        {
            await buffer.AppendAsync(PsiPacketizer.Packetize(0, DvbFixtures.PatService(10301, 5100, 0, 1), 0), cancellationToken);
            await buffer.AppendAsync(PsiPacketizer.Packetize(0, DvbFixtures.PatService(11110, 6100, 1, 1), 1), cancellationToken);
            using var reader = buffer.OpenReader(cancellationToken: cancellationToken);
            var prefix = new byte[376];
            await reader.ReadExactlyAsync(prefix, cancellationToken);
            var received = new List<PsiSectionEventArgs>();
            var assembler = new PsiSectionAssembler();
            assembler.SectionReceived += (_, section) => received.Add(section);
            assembler.Push(prefix.AsSpan(0, 188));
            assembler.Push(prefix.AsSpan(188, 188));
            Assert.Equal(2, received.Count);
            Assert.Equal(new ushort[] { 10301, 11110 }, received.SelectMany(section => ProgrammeMapParser.ParsePat(section.Data.Span).Keys));
        }
        finally
        {
            await buffer.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StartupCleanupDeletesOnlyAbandonedOwnedBuffers()
    {
        var root = Path.Combine(Path.GetTempPath(), "fritztv-cleanup-tests-" + Guid.NewGuid().ToString("N"));
        var owned = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var unrelated = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var buffer = new IndexedTimeshiftBuffer(owned, freeSpaceReserve: 0);
        try
        {
            Directory.CreateDirectory(unrelated);
            await File.WriteAllTextAsync(Path.Combine(unrelated, "important.txt"), "Unrelated user data", TestContext.Current.CancellationToken);
            Assert.Equal(0, TimeshiftBufferMaintenance.CleanAbandoned(root));
            await buffer.DisposeAsync();
            Assert.Equal(0, TimeshiftBufferMaintenance.CleanAbandoned(root));
            await File.WriteAllTextAsync(Path.Combine(owned, ".fritztv-buffer"), "{\"Version\":1,\"ProcessId\":2147483647,\"ProcessStartTicks\":0}", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(owned, "000000000001.ts"), "Synthetic test transport", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(owned, "keep.txt"), "Unknown extra data", TestContext.Current.CancellationToken);
            Assert.Equal(0, TimeshiftBufferMaintenance.CleanAbandoned(root));
            File.Delete(Path.Combine(owned, "keep.txt"));
            Assert.Equal(1, TimeshiftBufferMaintenance.CleanAbandoned(root));
            Assert.False(Directory.Exists(owned));
            Assert.True(File.Exists(Path.Combine(unrelated, "important.txt")));
        }
        finally
        {
            await buffer.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] Packet(byte marker)
    {
        var data = Enumerable.Repeat((byte)0xff, 188).ToArray();
        data[0] = 0x47;
        data[1] = 0x40;
        data[2] = 100;
        data[3] = 0x30;
        data[4] = 1;
        data[5] = 0x40;
        data[^1] = marker;
        return data;
    }
}
