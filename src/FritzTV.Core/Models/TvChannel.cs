namespace FritzTV.Core.Models;

public sealed record TvChannel(
    string Id,
    Guid DeviceId,
    string Name,
    ChannelKind Kind,
    Uri StreamUri,
    int? ServiceId = null,
    bool IsFavorite = false,
    DvbServiceIdentity? ServiceIdentity = null,
    bool IsAvailable = true,
    int SortOrder = int.MaxValue);
