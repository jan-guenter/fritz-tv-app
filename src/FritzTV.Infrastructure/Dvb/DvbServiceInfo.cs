namespace FritzTV.Infrastructure.Dvb;

public sealed record DvbServiceInfo(ushort ServiceId, byte ServiceType, string Name, string Provider, bool IsEncrypted);
