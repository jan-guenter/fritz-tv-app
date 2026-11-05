namespace FritzTV.Core.Models;

public sealed record RecordingDefinition(
    Guid Id,
    string ChannelId,
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    string TimeZoneId,
    Weekdays RepeatOn = Weekdays.None,
    TimeSpan? StartPadding = null,
    TimeSpan? EndPadding = null,
    ushort? EventId = null,
    bool RetainTransportStream = false)
{
    public TimeSpan EffectiveStartPadding => StartPadding ?? TimeSpan.FromMinutes(2);

    public TimeSpan EffectiveEndPadding => EndPadding ?? TimeSpan.FromMinutes(5);
}

