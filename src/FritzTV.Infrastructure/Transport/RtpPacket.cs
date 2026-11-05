using System.Buffers.Binary;

namespace FritzTV.Infrastructure.Transport;

public sealed record RtpPacket(ushort Sequence, uint Timestamp, uint Source, ReadOnlyMemory<byte> Payload)
{
    public static bool TryParse(ReadOnlyMemory<byte> datagram, out RtpPacket? packet)
    {
        packet = null;
        var span = datagram.Span;
        if (span.Length < 12 || span[0] >> 6 != 2 || (span[1] & 0x7f) != 33)
        {
            return false;
        }

        var offset = 12 + (4 * (span[0] & 15));
        if (offset > span.Length)
        {
            return false;
        }

        if ((span[0] & 16) != 0)
        {
            if (offset + 4 > span.Length)
            {
                return false;
            }

            offset += 4 + (4 * BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset + 2, 2)));
        }

        var end = span.Length;
        if ((span[0] & 32) != 0)
        {
            var padding = span[^1];
            if (padding == 0 || padding > end - offset)
            {
                return false;
            }

            end -= padding;
        }

        if (offset >= end || (end - offset) % 188 != 0 || span[offset] != 0x47)
        {
            return false;
        }

        packet = new RtpPacket(BinaryPrimitives.ReadUInt16BigEndian(span.Slice(2, 2)), BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4)), BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8, 4)), datagram[offset..end]);
        return true;
    }
}
