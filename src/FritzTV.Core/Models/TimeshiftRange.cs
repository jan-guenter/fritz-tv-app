namespace FritzTV.Core.Models;

public sealed record TimeshiftRange(DateTimeOffset Earliest, DateTimeOffset Live, long Bytes)
{
    public DateTimeOffset Clamp(DateTimeOffset position) => position < Earliest ? Earliest : position > Live ? Live : position;
}
