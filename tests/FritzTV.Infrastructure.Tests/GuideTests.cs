using System.Buffers.Binary;
using FritzTV.Core.Models;
using FritzTV.Infrastructure.Dvb;
using FritzTV.Infrastructure.Guide;
using FritzTV.Infrastructure.Persistence;
using FritzTV.Infrastructure.Transport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class GuideTests
{
    [Fact]
    public void CoverageHandlesSparseSegmentsAndRequiresEveryAdvertisedTable()
    {
        var tracker = new EitCoverageTracker();
        foreach (var number in new byte[] { 0, 1, 2, 8, 9 })
        {
            tracker.Observe(Section(number, 0x50));
        }

        Assert.False(tracker.IsComplete);
        foreach (var number in new byte[] { 0, 1, 2, 8, 9 })
        {
            tracker.Observe(Section(number, 0x51));
        }

        Assert.True(tracker.IsComplete);
        tracker.Observe(Section(0, 0x50, version: 1));
        Assert.False(tracker.IsComplete);
    }

    [Fact]
    public async Task GuideRejectsStaleVersionsAndPersistsActualTransportUpdates()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fritztv-guide-tests-" + Guid.NewGuid().ToString("N"));
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider();
        try
        {
            var repository = new AppRepository(Path.Combine(directory, "app.db"));
            await repository.InitializeAsync(cancellationToken);
            await using var sessions = new SatIpSessionManager();
            using var service = new GuideService(repository, sessions, NullLogger<GuideService>.Instance, clock);
            var programme = new Programme(new DvbServiceIdentity(Guid.NewGuid(), 1, 2, 3), 42, clock.GetUtcNow().AddHours(1), TimeSpan.FromHours(1), "Current", "Description", "eng", 0x50, 1);
            service.Ingest([programme]);
            service.Ingest([programme with { Version = 0, Title = "Stale version" }]);
            service.Ingest([programme with { IsActualTransport = false, Version = 2, Title = "Other transport" }]);
            service.Ingest([programme with { Version = 2, Title = "Updated" }]);
            await service.RefreshAsync(cancellationToken);
            var saved = Assert.Single(await repository.GetProgrammesAsync(clock.GetUtcNow(), clock.GetUtcNow().AddDays(1), cancellationToken));
            Assert.Equal("Updated", saved.Title);
            Assert.Equal(1, service.Status.ProgrammeCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static byte[] Section(byte number, byte table, int version = 0)
    {
        var section = DvbFixtures.Eit();
        section[0] = table;
        section[5] = (byte)(0xc1 | (version << 1));
        section[6] = number;
        section[7] = 9;
        section[12] = number < 8 ? (byte)2 : (byte)9;
        section[13] = 0x51;
        BinaryPrimitives.WriteUInt32BigEndian(section.AsSpan(section.Length - 4), MpegCrc32.Compute(section.AsSpan(0, section.Length - 4)));
        return section;
    }
}
