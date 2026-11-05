using System.Buffers.Binary;

namespace FritzTV.Infrastructure.Dvb;

public readonly record struct TsPacket(int Pid, int Continuity, bool StartsPayload, bool Discontinuity, bool RandomAccess, int PayloadOffset, long? Pcr)
{
    public static bool TryParse(ReadOnlySpan<byte> data, out TsPacket packet)
    {
        packet = default;
        if (data.Length != 188 || data[0] != 0x47 || (data[1] & 0x80) != 0)
        {
            return false;
        }

        var control = (data[3] >> 4) & 3;
        if (control == 0 || (data[3] & 0xc0) != 0)
        {
            return false;
        }

        var offset = control is 1 ? 4 : 188;
        var discontinuity = false;
        var randomAccess = false;
        long? pcr = null;
        if (control is 2 or 3)
        {
            var length = data[4];
            if (5 + length > 188)
            {
                return false;
            }

            offset = control == 3 ? 5 + length : 188;
            if (length > 0)
            {
                discontinuity = (data[5] & 0x80) != 0;
                randomAccess = (data[5] & 0x40) != 0;
                if ((data[5] & 0x10) != 0 && length >= 7)
                {
                    pcr = ((long)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(6, 4)) << 1) | ((uint)data[10] >> 7);
                }
            }
        }

        packet = new TsPacket(((data[1] & 31) << 8) | data[2], data[3] & 15, (data[1] & 64) != 0, discontinuity, randomAccess, offset, pcr);
        return true;
    }
}
