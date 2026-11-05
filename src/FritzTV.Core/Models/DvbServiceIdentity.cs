namespace FritzTV.Core.Models;

public sealed record DvbServiceIdentity(Guid DeviceId, ushort OriginalNetworkId, ushort TransportStreamId, ushort ServiceId);

