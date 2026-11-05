namespace FritzTV.Core.Models;

public sealed record Programme(
    DvbServiceIdentity Service,
    ushort EventId,
    DateTimeOffset Start,
    TimeSpan Duration,
    string Title,
    string Description,
    string Language,
    byte TableId = 0,
    byte Version = 0,
    bool IsActualTransport = true)
{
    public DateTimeOffset End => Start + Duration;
}
