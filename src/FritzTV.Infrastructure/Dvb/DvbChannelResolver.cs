using FritzTV.Core.Models;
using FritzTV.Infrastructure.Transport;

namespace FritzTV.Infrastructure.Dvb;

public sealed class DvbChannelResolver(Guid deviceId)
{
    private IReadOnlyDictionary<ushort, int> programmes = new Dictionary<ushort, int>();
    private DvbTransportInfo? transport;

    public bool HasTransportIdentity => transport is not null && programmes.Count > 0;

    public void Observe(PsiSectionEventArgs section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (section.Data.Span[0] == 0)
        {
            programmes = ProgrammeMapParser.ParsePat(section.Data.Span);
        }
        else if (section.Data.Span[0] == 0x42)
        {
            transport = SdtParser.Parse(section.Data.Span);
        }
    }

    public DvbServiceIdentity? Resolve(TvChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.DeviceId != deviceId || transport is null)
        {
            return null;
        }

        var pids = SatIpTuning.Pids(channel.StreamUri);
        var matching = programmes.Where(item => pids.Contains(item.Value)).Select(item => item.Key).ToArray();
        if (matching.Length != 1 || !transport.Services.Any(service => service.ServiceId == matching[0]))
        {
            return null;
        }

        return new DvbServiceIdentity(deviceId, transport.OriginalNetworkId, transport.TransportStreamId, matching[0]);
    }
}
