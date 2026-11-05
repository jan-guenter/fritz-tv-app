using System.Globalization;
using FritzTV.Infrastructure.Dvb;

namespace FritzTV.Probe;

internal sealed class ProbeStatistics
{
    private readonly PsiSectionAssembler assembler = new();

    public ProbeStatistics() => assembler.SectionReceived += OnSectionReceived;

    public Dictionary<int, long> Pids { get; } = [];

    public Dictionary<string, long> TableCounts { get; } = new(StringComparer.Ordinal);

    public Dictionary<int, int> Programs { get; } = [];

    public Dictionary<int, int> StreamTypes { get; } = [];

    public void Push(ReadOnlyMemory<byte> batch)
    {
        for (var offset = 0; offset + 188 <= batch.Length; offset += 188)
        {
            var packet = batch.Slice(offset, 188);
            if (!TsPacket.TryParse(packet.Span, out var ts))
            {
                continue;
            }

            Pids[ts.Pid] = Pids.GetValueOrDefault(ts.Pid) + 1;
            if (ts.Pid is 0 or 16 or 17 or 18 or 20 || Programs.ContainsValue(ts.Pid))
            {
                assembler.Push(packet.Span);
            }
        }
    }

    private void OnSectionReceived(object? sender, PsiSectionEventArgs section)
    {
        var data = section.Data.Span;
        var tableId = data[0].ToString("X2", CultureInfo.InvariantCulture);
        TableCounts[tableId] = TableCounts.GetValueOrDefault(tableId) + 1;
        if (data[0] == 0 && data.Length >= 12)
        {
            for (var position = 8; position + 4 <= data.Length - 4; position += 4)
            {
                var serviceId = (data[position] << 8) | data[position + 1];
                if (serviceId != 0)
                {
                    Programs[serviceId] = ((data[position + 2] & 31) << 8) | data[position + 3];
                }
            }
        }
        else if (data[0] == 2 && data.Length >= 16)
        {
            var position = 12 + (((data[10] & 15) << 8) | data[11]);
            while (position + 5 <= data.Length - 4)
            {
                StreamTypes[((data[position + 1] & 31) << 8) | data[position + 2]] = data[position];
                position += 5 + (((data[position + 3] & 15) << 8) | data[position + 4]);
            }
        }
    }
}
