using FritzTV.Core.Models;
using FritzTV.Core.Scheduling;
using Xunit;

namespace FritzTV.Core.Tests;

public sealed class ScheduleCalculatorTests
{
    [Fact]
    public void NonexistentBerlinTimeAdvancesByGap()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var actual = ScheduleCalculator.ResolveLocal(new DateTime(2026, 3, 29, 2, 30, 0), zone);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), actual);
    }

    [Fact]
    public void AmbiguousBerlinTimeUsesFirstOccurrence()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var actual = ScheduleCalculator.ResolveLocal(new DateTime(2026, 10, 25, 2, 30, 0), zone);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), actual);
    }

    [Fact]
    public void RepeatingTimerKeepsLocalWallTimeAcrossDst()
    {
        var start = new DateTimeOffset(2026, 3, 28, 19, 0, 0, TimeSpan.Zero);
        var definition = new RecordingDefinition(Guid.NewGuid(), "channel", "Evening programme", start, start.AddHours(1), "Europe/Berlin", Weekdays.EveryDay, TimeSpan.Zero, TimeSpan.Zero);
        var instances = ScheduleCalculator.Expand(definition, start, start.AddDays(2), start);
        Assert.Equal(3, instances.Count);
        Assert.Equal(start.AddHours(23), instances[1].Start);
        Assert.Equal(TimeSpan.FromHours(1), instances[1].End - instances[1].Start);
    }

    [Fact]
    public void SpringGapShiftsWholeOccurrenceWithoutInvertingTheEnd()
    {
        var start = new DateTimeOffset(2026, 3, 28, 1, 30, 0, TimeSpan.Zero);
        var definition = new RecordingDefinition(Guid.NewGuid(), "channel", "Gap programme", start, start.AddMinutes(45), "Europe/Berlin", Weekdays.EveryDay, TimeSpan.Zero, TimeSpan.Zero);
        var actual = Assert.Single(ScheduleCalculator.Expand(definition, new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 30, 0, 0, 0, TimeSpan.Zero), start));
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), actual.Start);
        Assert.Equal(TimeSpan.FromMinutes(45), actual.End - actual.Start);
    }

    [Fact]
    public void FirstOccurrenceInsideSpringGapRetainsOriginalWallTimeForFollowingDays()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var localStart = new DateTime(2030, 3, 31, 2, 30, 0, DateTimeKind.Unspecified);
        var localEnd = localStart.AddMinutes(45);
        var first = ScheduleCalculator.ResolveLocalInterval(localStart, localEnd, zone);
        var definition = new RecordingDefinition(Guid.NewGuid(), "channel", "First gap date", first.Start, first.End, zone.Id,
            Weekdays.EveryDay, TimeSpan.Zero, TimeSpan.Zero, RecurrenceLocalStart: localStart, RecurrenceLocalEnd: localEnd);
        var actual = ScheduleCalculator.Expand(definition, first.Start.AddDays(-2), first.End.AddDays(1), first.Start);
        Assert.Equal(2, actual.Count);
        Assert.Equal(first.Start, actual[0].Start);
        Assert.Equal(TimeSpan.FromMinutes(45), actual[0].End - actual[0].Start);
        Assert.Equal(new DateTime(2030, 4, 1, 2, 30, 0, DateTimeKind.Unspecified), TimeZoneInfo.ConvertTime(actual[1].Start, zone).DateTime);
        Assert.Equal(TimeSpan.FromMinutes(45), actual[1].End - actual[1].Start);
    }

    [Fact]
    public void OneOffTimerIncludesPaddingAndRejectsExpiredInterval()
    {
        var start = new DateTimeOffset(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
        var definition = new RecordingDefinition(Guid.NewGuid(), "channel", "Programme", start, start.AddHours(1), "Europe/Berlin");
        var instance = Assert.Single(ScheduleCalculator.Expand(definition, start.AddDays(-1), start.AddDays(1), start));
        Assert.Equal(start.AddMinutes(-2), instance.Start);
        Assert.Equal(start.AddHours(1).AddMinutes(5), instance.End);
        Assert.Empty(ScheduleCalculator.Expand(definition, instance.End, instance.End.AddDays(1), start));
        Assert.Equal(RecordingState.Missed, ScheduleCalculator.GetRecoveryState(instance, instance.End));
    }
}
