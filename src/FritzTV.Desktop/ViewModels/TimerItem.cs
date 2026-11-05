using System.Globalization;
using FritzTV.Core.Models;
using FritzTV.Desktop.Localization;

namespace FritzTV.Desktop.ViewModels;

public sealed record TimerItem(RecordingDefinition Definition, UiText Text)
{
    private static readonly (Weekdays Flag, string Key)[] Days = [(Weekdays.Monday, "Monday"), (Weekdays.Tuesday, "Tuesday"), (Weekdays.Wednesday, "Wednesday"), (Weekdays.Thursday, "Thursday"), (Weekdays.Friday, "Friday"), (Weekdays.Saturday, "Saturday"), (Weekdays.Sunday, "Sunday")];

    public string Title => Definition.Title;

    public string Schedule
    {
        get
        {
            TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(Definition.TimeZoneId);
            string repetition = Definition.RepeatOn == Weekdays.None ? Text["Once"] : string.Join(", ", Days.Where(day => (Definition.RepeatOn & day.Flag) != Weekdays.None).Select(day => Text[day.Key]));
            DateTimeOffset firstStart = TimeZoneInfo.ConvertTime(Definition.Start, zone);
            DateTimeOffset firstEnd = TimeZoneInfo.ConvertTime(Definition.End, zone);
            string start = firstStart.ToString("g", CultureInfo.CurrentCulture) + " " + firstStart.ToString("zzz", CultureInfo.InvariantCulture);
            string end = firstEnd.ToString("g", CultureInfo.CurrentCulture) + " " + firstEnd.ToString("zzz", CultureInfo.InvariantCulture);
            if (Definition.RepeatOn != Weekdays.None && Definition.RecurrenceLocalStart is { } localStart && Definition.RecurrenceLocalEnd is { } localEnd)
            {
                start = localStart.ToString("g", CultureInfo.CurrentCulture);
                end = localEnd.ToString("g", CultureInfo.CurrentCulture);
            }

            return string.Format(CultureInfo.CurrentCulture, Text["TimerSchedule"], start, end, repetition, zone.DisplayName);
        }
    }
}
