using System.Buffers.Binary;

namespace FritzTV.Infrastructure.Dvb;

public static class SdtParser
{
    public static DvbTransportInfo? Parse(ReadOnlySpan<byte> section)
    {
        if (section.Length < 15 || section[0] != 0x42 || (section[5] & 1) == 0
            || section.Length != 3 + (((section[1] & 15) << 8) | section[2]) || MpegCrc32.Compute(section) != 0)
        {
            return null;
        }

        var services = new List<DvbServiceInfo>();
        var position = 11;
        while (position + 5 <= section.Length - 4)
        {
            var descriptorLength = ((section[position + 3] & 15) << 8) | section[position + 4];
            if (position + 5 + descriptorLength > section.Length - 4)
            {
                return null;
            }

            var text = ReadServiceDescriptor(section.Slice(position + 5, descriptorLength));
            services.Add(new DvbServiceInfo(BinaryPrimitives.ReadUInt16BigEndian(section.Slice(position, 2)), text.Type, text.Name, text.Provider, (section[position + 3] & 0x10) != 0, (section[position + 2] & 2) != 0, (section[position + 2] & 1) != 0));
            position += 5 + descriptorLength;
        }

        return new DvbTransportInfo(BinaryPrimitives.ReadUInt16BigEndian(section.Slice(8, 2)), BinaryPrimitives.ReadUInt16BigEndian(section.Slice(3, 2)), services);
    }

    private static (byte Type, string Name, string Provider) ReadServiceDescriptor(ReadOnlySpan<byte> descriptors)
    {
        while (descriptors.Length >= 2)
        {
            var length = descriptors[1];
            if (length + 2 > descriptors.Length)
            {
                break;
            }

            if (descriptors[0] == 0x48 && length >= 3)
            {
                var body = descriptors.Slice(2, length);
                var providerLength = body[1];
                if (2 + providerLength < body.Length)
                {
                    var nameLength = body[2 + providerLength];
                    if (3 + providerLength + nameLength <= body.Length)
                    {
                        return (body[0], DvbText.Decode(body.Slice(3 + providerLength, nameLength)), DvbText.Decode(body.Slice(2, providerLength)));
                    }
                }
            }

            descriptors = descriptors[(length + 2)..];
        }

        return (0, string.Empty, string.Empty);
    }
}
