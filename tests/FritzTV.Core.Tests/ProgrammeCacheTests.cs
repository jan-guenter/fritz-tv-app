using FritzTV.Core.Guide;
using FritzTV.Core.Models;
using Xunit;

namespace FritzTV.Core.Tests;

public sealed class ProgrammeCacheTests
{
    [Fact]
    public void CacheBoundsMemoryAndPrunesExpiredEventsWithoutAcceptingOlderVersions()
    {
        var clock = new MutableTimeProvider();
        var cache = new ProgrammeCache(2, clock);
        var programme = new Programme(new DvbServiceIdentity(Guid.NewGuid(), 1, 2, 3), 1, clock.Now.AddHours(1), TimeSpan.FromHours(1), "Current", "", "eng", 0x50, 1);
        cache.Ingest([programme with { Start = clock.Now.AddDays(-3) }, programme with { Start = clock.Now.AddDays(15) }, programme]);
        cache.Ingest([programme with { Version = 0, Title = "Stale" }, programme with { IsActualTransport = false, Version = 2, Title = "Other transport" }]);
        Assert.Equal("Current", Assert.Single(cache.Snapshot()).Title);
        cache.Ingest([programme with { EventId = 2 }, programme with { EventId = 3 }]);
        Assert.Equal(2, cache.Snapshot().Count);
        clock.Now = clock.Now.AddDays(3);
        cache.Ingest([programme with { EventId = 3, Start = clock.Now.AddHours(1) }]);
        Assert.Equal((ushort)3, Assert.Single(cache.Snapshot()).EventId);
    }

    [Fact]
    public void VersionWrapAndReusedExpiredEventIdsCanAdvance()
    {
        var now = new DateTimeOffset(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
        var previous = new Programme(new DvbServiceIdentity(Guid.NewGuid(), 1, 2, 3), 1, now, TimeSpan.FromHours(1), "Old", "", "eng", 0x50, 31);
        Assert.True(ProgrammeUpdatePolicy.ShouldReplace(previous, previous with { Version = 0 }, now));
        Assert.False(ProgrammeUpdatePolicy.ShouldReplace(previous with { Version = 0 }, previous, now));
        previous = previous with { Start = now.AddHours(-2), Version = 15 };
        Assert.True(ProgrammeUpdatePolicy.ShouldReplace(previous, previous with { Start = now.AddHours(1), Version = 0 }, now));
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
