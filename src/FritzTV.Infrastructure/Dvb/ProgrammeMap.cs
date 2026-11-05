namespace FritzTV.Infrastructure.Dvb;

public sealed record ProgrammeMap(ushort ServiceId, int PmtPid, int PcrPid, IReadOnlyList<ElementaryStreamInfo> Streams);
