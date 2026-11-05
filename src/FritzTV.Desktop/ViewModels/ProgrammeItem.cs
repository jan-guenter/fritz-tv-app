using System.Globalization;
using Avalonia;
using FritzTV.Core.Models;

namespace FritzTV.Desktop.ViewModels;

public sealed record ProgrammeItem(Programme Programme, double TimelineWidth = 180, double GapBefore = 0, TimeZoneInfo? DisplayZone = null)
{
    public string Title => Programme.Title;

    public string Description => Programme.Description;

    public DateTime LocalStart => TimeZoneInfo.ConvertTime(Programme.Start, DisplayZone ?? TimeZoneInfo.Local).DateTime;

    public DateTime LocalEnd => TimeZoneInfo.ConvertTime(Programme.End, DisplayZone ?? TimeZoneInfo.Local).DateTime;

    public string TimeLabel => LocalStart.ToString("g", CultureInfo.CurrentCulture) + " – " + LocalEnd.ToString("t", CultureInfo.CurrentCulture);

    public Thickness TimelineMargin => new(GapBefore, 0, 4, 0);
}
