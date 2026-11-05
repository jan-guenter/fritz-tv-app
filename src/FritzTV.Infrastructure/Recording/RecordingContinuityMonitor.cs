using FritzTV.Infrastructure.Dvb;

namespace FritzTV.Infrastructure.Recording;

public sealed class RecordingContinuityMonitor
{
    private readonly Dictionary<int, int> counters = [];
    private readonly HashSet<int> programmeMaps = [];
    private readonly byte[] packet = new byte[188];
    private int buffered;

    public long Problems { get; private set; }

    public void Inspect(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            var count = Math.Min(188 - buffered, bytes.Length);
            bytes[..count].CopyTo(packet.AsSpan(buffered));
            buffered += count;
            bytes = bytes[count..];
            if (buffered == 188)
            {
                InspectPacket();
                buffered = 0;
            }
        }
    }

    private void InspectPacket()
    {
        if (!TsPacket.TryParse(packet, out var ts))
        {
            Problems++;
            return;
        }

        if (ts.StartsPayload && ts.PayloadOffset + 3 < 188 && packet[ts.PayloadOffset] == 0 && packet[ts.PayloadOffset + 1] == 2 && (packet[ts.PayloadOffset + 2] & 0xb0) == 0xb0)
        {
            programmeMaps.Add(ts.Pid);
        }

        if (ts.Pid is 0 or 1 or 16 or 17 or 18 or 20 or 8191 || programmeMaps.Contains(ts.Pid))
        {
            return;
        }

        if (ts.Discontinuity)
        {
            counters.Remove(ts.Pid);
        }

        if (ts.PayloadOffset >= 188)
        {
            return;
        }

        if (counters.TryGetValue(ts.Pid, out var previous) && ts.Continuity != previous && ts.Continuity != ((previous + 1) & 15))
        {
            Problems++;
        }

        counters[ts.Pid] = ts.Continuity;
    }
}
