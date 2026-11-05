using System.Buffers.Binary;
using System.Text;

namespace FritzTV.Infrastructure.Dvb;

public static class ProgrammeMapParser
{
    public static IReadOnlyDictionary<ushort, int> ParsePat(ReadOnlySpan<byte> section)
    {
        var result = new Dictionary<ushort, int>();
        if (!IsValid(section, 0, 12) || (section.Length - 12) % 4 != 0)
        {
            return result;
        }

        for (var index = 8; index + 4 <= section.Length - 4; index += 4)
        {
            var serviceId = BinaryPrimitives.ReadUInt16BigEndian(section.Slice(index, 2));
            if (serviceId != 0)
            {
                result[serviceId] = ((section[index + 2] & 31) << 8) | section[index + 3];
            }
        }

        return result;
    }

    public static ProgrammeMap? ParsePmt(int pid, ReadOnlySpan<byte> section)
    {
        if (!IsValid(section, 2, 16))
        {
            return null;
        }

        var streams = new List<ElementaryStreamInfo>();
        var position = 12 + (((section[10] & 15) << 8) | section[11]);
        if (position > section.Length - 4)
        {
            return null;
        }
        while (position + 5 <= section.Length - 4)
        {
            var descriptorLength = ((section[position + 3] & 15) << 8) | section[position + 4];
            if (position + 5 + descriptorLength > section.Length - 4)
            {
                return null;
            }

            var descriptors = section.Slice(position + 5, descriptorLength);
            var language = string.Empty;
            var teletext = false;
            var subtitles = false;
            while (descriptors.Length >= 2)
            {
                var size = descriptors[1];
                if (size + 2 > descriptors.Length)
                {
                    return null;
                }

                if (descriptors[0] == 0x0a && size >= 4)
                {
                    language = Encoding.ASCII.GetString(descriptors.Slice(2, 3));
                }

                teletext |= descriptors[0] == 0x56;
                subtitles |= descriptors[0] == 0x59;
                descriptors = descriptors[(size + 2)..];
            }

            if (!descriptors.IsEmpty)
            {
                return null;
            }

            streams.Add(new ElementaryStreamInfo(((section[position + 1] & 31) << 8) | section[position + 2], section[position], language, teletext, subtitles));
            position += 5 + descriptorLength;
        }

        return position != section.Length - 4 ? null : new ProgrammeMap(BinaryPrimitives.ReadUInt16BigEndian(section.Slice(3, 2)), pid, ((section[8] & 31) << 8) | section[9], streams);
    }

    private static bool IsValid(ReadOnlySpan<byte> section, byte tableId, int minimum) => section.Length >= minimum && section[0] == tableId
        && section.Length == 3 + (((section[1] & 15) << 8) | section[2]) && (section[5] & 1) != 0 && MpegCrc32.Compute(section) == 0;
}
