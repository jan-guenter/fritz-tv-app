using FritzTV.Core.Models;

namespace FritzTV.Core.Scheduling;

public static class ScheduleCalculator
{
    public static DateTimeOffset ResolveLocal(DateTime localTime, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        localTime = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(localTime))
        {
            var before = localTime.AddHours(-1);
            var after = localTime.AddHours(1);
            while (zone.IsInvalidTime(before))
            {
                before = before.AddHours(-1);
            }

            while (zone.IsInvalidTime(after))
            {
                after = after.AddHours(1);
            }

            localTime += zone.GetUtcOffset(after) - zone.GetUtcOffset(before);
        }

        var offset = zone.IsAmbiguousTime(localTime)
            ? zone.GetAmbiguousTimeOffsets(localTime).Max()
            : zone.GetUtcOffset(localTime);
        return new DateTimeOffset(localTime, offset).ToUniversalTime();
    }

    public static IReadOnlyList<RecordingInstance> Expand(RecordingDefinition definition, DateTimeOffset from, DateTimeOffset until, DateTimeOffset created)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.End <= definition.Start)
        {
            throw new ArgumentException("A recording must end after it starts.", nameof(definition));
        }

        if (definition.EffectiveStartPadding < TimeSpan.Zero || definition.EffectiveEndPadding < TimeSpan.Zero)
        {
            throw new ArgumentException("Recording padding cannot be negative.", nameof(definition));
        }

        var result = new List<RecordingInstance>();
        if (definition.RepeatOn == Weekdays.None)
        {
            Add(definition.Start, definition.End);
            return result;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(definition.TimeZoneId);
        var localStart = TimeZoneInfo.ConvertTime(definition.Start, zone).DateTime;
        var localEnd = TimeZoneInfo.ConvertTime(definition.End, zone).DateTime;
        var wallDuration = localEnd - localStart;
        var firstDate = TimeZoneInfo.ConvertTime(from - definition.EffectiveEndPadding - (definition.End - definition.Start), zone).Date;
        var lastDate = TimeZoneInfo.ConvertTime(until + definition.EffectiveStartPadding, zone).Date;
        for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            if ((definition.RepeatOn & (Weekdays)(1 << (int)date.DayOfWeek)) == Weekdays.None)
            {
                continue;
            }

            var start = date + localStart.TimeOfDay;
            if (start < localStart)
            {
                continue;
            }

            Add(ResolveLocal(start, zone), ResolveLocal(start + wallDuration, zone));
        }

        return result;

        void Add(DateTimeOffset start, DateTimeOffset end)
        {
            start -= definition.EffectiveStartPadding;
            end += definition.EffectiveEndPadding;
            if (end > from && start < until)
            {
                result.Add(new RecordingInstance(Guid.NewGuid(), definition.Id, definition.ChannelId, definition.Title, start, end, created, RecordingState.Scheduled));
            }
        }
    }

    public static RecordingState GetRecoveryState(RecordingInstance instance, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.End <= now)
        {
            return instance.State == RecordingState.Recording ? RecordingState.Partial : RecordingState.Missed;
        }

        return instance.Start <= now ? RecordingState.Partial : RecordingState.Scheduled;
    }
}
