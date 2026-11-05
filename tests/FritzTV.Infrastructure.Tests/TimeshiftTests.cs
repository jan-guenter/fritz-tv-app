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
