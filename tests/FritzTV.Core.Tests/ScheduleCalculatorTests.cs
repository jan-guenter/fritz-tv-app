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
