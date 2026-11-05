using FritzTV.Core.Models;
using FritzTV.Infrastructure.Transport;

namespace FritzTV.Infrastructure.Dvb;

public sealed class DvbChannelResolver(Guid deviceId)
{
    private readonly PsiTableSnapshot pat = new();
    private readonly PsiTableSnapshot sdt = new();
    private Dictionary<ushort, int> programmes = [];
    private DvbTransportInfo? transport;

    public IReadOnlyList<ushort> ExpectedScheduleServices => transport?.Services.Where(service => service.HasSchedule).Select(service => service.ServiceId).ToArray() ?? [];

    public bool HasTransportIdentity => transport is not null && pat.TableIdExtension == transport.TransportStreamId && programmes.Count > 0;

    public void Observe(PsiSectionEventArgs section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (section.Data.IsEmpty)
        {
            return;
        }

        if (section.Data.Span[0] == 0 && pat.Observe(section.Data.Span))
        {
            programmes = pat.Sections.SelectMany(value => ProgrammeMapParser.ParsePat(value.Span)).GroupBy(item => item.Key).ToDictionary(group => group.Key, group => group.Last().Value);
        }
        else if (section.Data.Span[0] == 0x42 && sdt.Observe(section.Data.Span))
        {
            var tables = sdt.Sections.Select(value => SdtParser.Parse(value.Span)).OfType<DvbTransportInfo>().ToArray();
            transport = tables.Length == 0 ? null : tables[0] with { Services = tables.SelectMany(value => value.Services).DistinctBy(value => value.ServiceId).ToArray() };
        }
    }

    public DvbServiceIdentity? Resolve(TvChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.DeviceId != deviceId || !HasTransportIdentity || transport is null)
        {
            return null;
        }

        var pids = SatIpTuning.Pids(channel.StreamUri);
        var matching = channel.ServiceId is { } service && programmes.ContainsKey((ushort)service)
            ? [(ushort)service] : programmes.Where(item => pids.Contains(item.Value)).Select(item => item.Key).ToArray();
        if (matching.Length != 1 || !transport.Services.Any(service => service.ServiceId == matching[0]))
        {
            return null;
        }

        return new DvbServiceIdentity(deviceId, transport.OriginalNetworkId, transport.TransportStreamId, matching[0]);
    }
}
