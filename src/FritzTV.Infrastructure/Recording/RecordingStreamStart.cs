using FritzTV.Core.Models;
using FritzTV.Infrastructure.Dvb;

namespace FritzTV.Infrastructure.Recording;

public sealed class RecordingStreamStart
{
    private readonly ChannelKind kind;
    private readonly PsiSectionAssembler assembler = new();
    private readonly PsiTableSnapshot pat = new();
    private readonly HashSet<int> pmtPids = [];
    private ProgrammeMap? programme;
    private byte[]? pmt;
    private int requiredPatSections = int.MaxValue;
    private int? targetPmtPid;
    private bool started;

    public RecordingStreamStart(ChannelKind kind, int? serviceId = null)
    {
        this.kind = kind;
        ServiceId = serviceId;
        assembler.SectionReceived += OnSectionReceived;
    }

    public int? ServiceId { get; private set; }

    public ProgrammeMap? Programme => programme;

    public IReadOnlySet<int>? RequiredPids { get; private set; }

    public IReadOnlyList<ReadOnlyMemory<byte>> Push(ReadOnlyMemory<byte> batch)
    {
        if (batch.Length % 188 != 0)
        {
            throw new InvalidDataException("Recording input must contain complete transport packets.");
        }

        Inspect(batch);
        if (started)
        {
            return [batch];
        }

        var tables = pat.Sections;
        var offset = FindEntryPoint(batch);
        if (offset is null || tables.Count != requiredPatSections || pmt is null || programme is null || programme.PmtPid != targetPmtPid)
        {
            return [];
        }

        var output = new List<ReadOnlyMemory<byte>>(tables.Count + 2);
        var continuity = 0;
        foreach (var section in tables)
        {
            var packets = PsiPacketizer.Packetize(0, section.Span, continuity);
            output.Add(packets);
            continuity = (continuity + packets.Length / 188) & 15;
        }

        output.Add(PsiPacketizer.Packetize(programme.PmtPid, pmt));
        output.Add(batch[offset.Value..]);
        started = true;
        return output;
    }

    private void Inspect(ReadOnlyMemory<byte> batch)
    {
        for (var offset = 0; offset + 188 <= batch.Length; offset += 188)
        {
            var packet = batch.Span.Slice(offset, 188);
            if (TsPacket.TryParse(packet, out var ts) && (ts.Pid == 0 || pmtPids.Contains(ts.Pid)))
            {
                assembler.Push(packet);
            }
        }
    }

    private int? FindEntryPoint(ReadOnlyMemory<byte> batch)
    {
        if (programme is null)
        {
            return null;
        }

        var video = programme.Streams.FirstOrDefault(item => item.StreamType is 0x01 or 0x02 or 0x10 or 0x1b or 0x24)?.Pid;
        var audio = programme.Streams.FirstOrDefault(item => (item.StreamType is 0x03 or 0x04 or 0x0f or 0x11 or 0x06 or 0x81 or 0x87) && !item.IsTeletext && !item.IsDvbSubtitle)?.Pid;
        for (var offset = 0; offset + 188 <= batch.Length; offset += 188)
        {
            if (TsPacket.TryParse(batch.Span.Slice(offset, 188), out var packet) && packet.StartsPayload
                && (kind == ChannelKind.Radio ? packet.Pid == audio : packet.Pid == video && packet.RandomAccess))
            {
                return offset;
            }
        }

        return null;
    }

    private void OnSectionReceived(object? sender, PsiSectionEventArgs section)
    {
        if (section.Data.Span[0] == 0 && pat.Observe(section.Data.Span))
        {
            requiredPatSections = section.Data.Span[7] + 1;
            pmtPids.Clear();
            foreach (var table in pat.Sections)
            {
                foreach (var pair in ProgrammeMapParser.ParsePat(table.Span))
                {
                    pmtPids.Add(pair.Value);
                }
            }

            RefreshRequiredPids();
        }
        else if (section.Data.Span[0] == 2 && pmtPids.Contains(section.Pid) && ProgrammeMapParser.ParsePmt(section.Pid, section.Data.Span) is { } map
            && (ServiceId is null || ServiceId == map.ServiceId) && (programme?.PmtPid != section.Pid || pmt is null || !pmt.AsSpan().SequenceEqual(section.Data.Span)))
        {
            ServiceId = map.ServiceId;
            programme = map;
            pmt = section.Data.ToArray();
            RefreshRequiredPids();
        }
    }

    private void RefreshRequiredPids()
    {
        if (ServiceId is { } service)
        {
            foreach (var section in pat.Sections)
            {
                if (ProgrammeMapParser.ParsePat(section.Span).TryGetValue((ushort)service, out var pid))
                {
                    targetPmtPid = pid;
                    break;
                }
            }
        }

        var next = programme?.Streams.Select(stream => stream.Pid).Append(programme.PmtPid).Append(programme.PcrPid).ToHashSet() ?? [];
        if (targetPmtPid is { } target)
        {
            next.Add(target);
        }

        if (next.Count > 0 && (RequiredPids is null || !RequiredPids.SetEquals(next)))
        {
            RequiredPids = next;
        }
    }
}
