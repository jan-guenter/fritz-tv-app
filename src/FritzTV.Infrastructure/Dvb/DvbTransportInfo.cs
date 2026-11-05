namespace FritzTV.Infrastructure.Dvb;

public sealed record DvbTransportInfo(ushort OriginalNetworkId, ushort TransportStreamId, IReadOnlyList<DvbServiceInfo> Services);
