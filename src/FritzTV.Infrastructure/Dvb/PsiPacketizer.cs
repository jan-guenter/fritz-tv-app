namespace FritzTV.Infrastructure.Dvb;

public static class PsiPacketizer
{
    public static byte[] Packetize(int pid, ReadOnlySpan<byte> section)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pid);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pid, 8191);
        if (section.Length is < 3 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(section));
        }

        var packetCount = (section.Length + 184) / 184;
        var output = new byte[packetCount * 188];
        output.AsSpan().Fill(0xff);
        var position = 0;
        for (var index = 0; index < packetCount; index++)
        {
            var packet = output.AsSpan(index * 188, 188);
            packet[0] = 0x47;
            packet[1] = (byte)((pid >> 8) | (index == 0 ? 0x40 : 0));
            packet[2] = (byte)pid;
            packet[3] = (byte)(0x10 | (index & 15));
            var payload = index == 0 ? 5 : 4;
            if (index == 0)
            {
                packet[4] = 0;
            }

            var count = Math.Min(section.Length - position, 188 - payload);
            section.Slice(position, count).CopyTo(packet[payload..]);
            position += count;
        }

        return output;
    }
}
