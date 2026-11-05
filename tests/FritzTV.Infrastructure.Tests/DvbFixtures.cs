using System.Buffers.Binary;
using System.Text;
using FritzTV.Infrastructure.Dvb;

namespace FritzTV.Infrastructure.Tests;

internal static class DvbFixtures
{
    public static byte[] Eit()
    {
        var title = Encoding.UTF8.GetBytes("Synthetic evening news");
        var description = Encoding.UTF8.GetBytes("Original test programme: Grüße");
        var descriptor = new List<byte> { 0x4d, (byte)(7 + title.Length + description.Length), (byte)'d', (byte)'e', (byte)'u', (byte)(title.Length + 1), 0x15 };
        descriptor.AddRange(title);
        descriptor.Add((byte)(description.Length + 1));
        descriptor.Add(0x15);
        descriptor.AddRange(description);
        var data = new List<byte> { 0x50, 0xb0, 0, 0x28, 0x3d, 0xc1, 0, 0, 0x04, 0x4d, 0x00, 0x01, 0, 0x50, 0x12, 0x34 };
        var mjd = (ushort)(new DateTime(2026, 11, 5) - new DateTime(1858, 11, 17)).Days;
        data.Add((byte)(mjd >> 8));
        data.Add((byte)mjd);
        data.AddRange([0x20, 0x15, 0x00, 0x00, 0x45, 0x00, (byte)(0x80 | (descriptor.Count >> 8)), (byte)descriptor.Count]);
        data.AddRange(descriptor);
        return CompleteSection(data);
    }

    public static byte[] Pat(int padding = 0)
    {
        var data = new List<byte> { 0, 0xb0, 0, 0x04, 0x4d, 0xc1, 0, 0, 0x28, 0x3d, 0xf3, 0xec };
        for (var index = 0; index < padding; index++)
        {
            data.AddRange([0, 0, 0xe0, 0x10]);
        }

        return CompleteSection(data);
    }

    public static IReadOnlyList<byte[]> Packetize(byte[] section, int pid, int continuity = 0)
    {
        var packets = new List<byte[]>();
        var position = 0;
        while (position < section.Length)
        {
            var packet = Enumerable.Repeat((byte)0xff, 188).ToArray();
            packet[0] = 0x47;
            packet[1] = (byte)((pid >> 8) | (position == 0 ? 0x40 : 0));
            packet[2] = (byte)pid;
            packet[3] = (byte)(0x10 | (continuity++ & 15));
            var start = position == 0 ? 5 : 4;
            if (position == 0)
            {
                packet[4] = 0;
            }

            var count = Math.Min(section.Length - position, 188 - start);
            section.AsSpan(position, count).CopyTo(packet.AsSpan(start));
            position += count;
            packets.Add(packet);
        }

        return packets;
    }

    private static byte[] CompleteSection(List<byte> data)
    {
        var length = data.Count + 1;
        data[1] = (byte)(0xb0 | (length >> 8));
        data[2] = (byte)length;
        var crc = MpegCrc32.Compute(data.ToArray());
        var result = new byte[data.Count + 4];
        data.CopyTo(result);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(data.Count), crc);
        return result;
    }
}
