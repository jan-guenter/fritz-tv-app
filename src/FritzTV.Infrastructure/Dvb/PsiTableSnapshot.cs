using System.Buffers.Binary;

namespace FritzTV.Infrastructure.Dvb;

public sealed class PsiTableSnapshot
{
    private readonly SortedDictionary<byte, byte[]> sections = [];
    private byte? version;
    private byte? tableId;
    private byte lastSection;

    public ushort? TableIdExtension { get; private set; }

    public IReadOnlyList<ReadOnlyMemory<byte>> Sections => sections.Values.Select(value => new ReadOnlyMemory<byte>(value)).ToArray();

    public bool Observe(ReadOnlySpan<byte> section)
    {
        if (section.Length < 12 || (section[1] & 0x80) == 0 || (section[5] & 1) == 0 || section[6] > section[7]
            || section.Length != 3 + (((section[1] & 15) << 8) | section[2]) || MpegCrc32.Compute(section) != 0)
        {
            return false;
        }

        var incomingVersion = (byte)((section[5] >> 1) & 31);
        var extension = BinaryPrimitives.ReadUInt16BigEndian(section[3..]);
        if (tableId == section[0] && TableIdExtension == extension && version is { } previous && previous != incomingVersion && (incomingVersion - previous + 32) % 32 >= 16)
        {
            return false;
        }

        if (tableId != section[0] || TableIdExtension != extension || version != incomingVersion)
        {
            sections.Clear();
            tableId = section[0];
            TableIdExtension = extension;
            version = incomingVersion;
            lastSection = section[7];
        }

        if (section[7] != lastSection)
        {
            return false;
        }

        sections[section[6]] = section.ToArray();
        return true;
    }
}
