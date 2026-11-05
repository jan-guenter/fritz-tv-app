namespace FritzTV.Core.Models;

public sealed record Programme(
    DvbServiceIdentity Service,
    ushort EventId,
    DateTimeOffset Start,
    TimeSpan Duration,
    string Title,
    string Description,
    string Language)
{
    public DateTimeOffset End => Start + Duration;
}
