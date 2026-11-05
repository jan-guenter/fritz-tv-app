using System.Buffers.Binary;
using System.Text;
using FritzTV.Core.Models;

namespace FritzTV.Infrastructure.Dvb;

public static class EitParser
{
    public static IReadOnlyList<Programme> Parse(Guid deviceId, ReadOnlySpan<byte> section, string preferredLanguage = "deu")
    {
        var result = new List<Programme>();
        if (section.Length < 18 || section[0] is < 0x4e or > 0x6f || (section[5] & 1) == 0
            || section.Length != 3 + (((section[1] & 15) << 8) | section[2]) || MpegCrc32.Compute(section) != 0)
        {
            return result;
        }

        var identity = new DvbServiceIdentity(deviceId, Read16(section, 10), Read16(section, 8), Read16(section, 3));
        var position = 14;
        while (position + 12 <= section.Length - 4)
        {
            var length = 12 + (((section[position + 10] & 15) << 8) | section[position + 11]);
            if (position + length > section.Length - 4)
            {
                break;
            }

            var data = section.Slice(position, length);
            position += length;
            if (TryGetTime(data, out var start, out var duration))
            {
                var text = ReadDescriptors(data[12..], preferredLanguage);
                result.Add(new Programme(identity, Read16(data, 0), start, duration, text.Title, text.Description, text.Language));
            }
        }

        return result;
    }

    private static bool TryGetTime(ReadOnlySpan<byte> data, out DateTimeOffset start, out TimeSpan duration)
    {
        start = default;
        duration = default;
        var hour = Bcd(data[4]);
        var minute = Bcd(data[5]);
        var second = Bcd(data[6]);
        var durationHour = Bcd(data[7]);
        var durationMinute = Bcd(data[8]);
        var durationSecond = Bcd(data[9]);
        if (hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59
            || durationHour < 0 || durationMinute is < 0 or > 59 || durationSecond is < 0 or > 59)
        {
            return false;
        }

        start = new DateTimeOffset(1858, 11, 17, 0, 0, 0, TimeSpan.Zero).AddDays(Read16(data, 2)).AddHours(hour).AddMinutes(minute).AddSeconds(second);
        duration = TimeSpan.FromHours(durationHour) + TimeSpan.FromMinutes(durationMinute) + TimeSpan.FromSeconds(durationSecond);
        return true;
    }

    private static (string Title, string Description, string Language) ReadDescriptors(ReadOnlySpan<byte> descriptors, string preferredLanguage)
    {
        var shortTexts = new Dictionary<string, (string Title, string Description)>(StringComparer.Ordinal);
        var extended = new Dictionary<string, SortedDictionary<int, string>>(StringComparer.Ordinal);
        while (descriptors.Length >= 2)
        {
            var length = descriptors[1];
            if (length + 2 > descriptors.Length)
            {
                break;
            }

            var body = descriptors.Slice(2, length);
            switch (descriptors[0])
            {
                case 0x4d when length >= 5:
                    ReadShort(body, shortTexts);
                    break;
                case 0x4e when length >= 6:
                    ReadExtended(body, extended);
                    break;
            }

            descriptors = descriptors[(length + 2)..];
        }

        var language = shortTexts.ContainsKey(preferredLanguage) ? preferredLanguage : shortTexts.Keys.FirstOrDefault() ?? extended.Keys.FirstOrDefault() ?? string.Empty;
        var shortText = shortTexts.GetValueOrDefault(language);
        var extra = extended.TryGetValue(language, out var pieces) ? string.Join(Environment.NewLine, pieces.Values) : string.Empty;
        var description = string.Join(Environment.NewLine, new[] { shortText.Description, extra }.Where(item => !string.IsNullOrWhiteSpace(item)));
        return (shortText.Title ?? string.Empty, description, language);
    }

    private static void ReadShort(ReadOnlySpan<byte> body, Dictionary<string, (string Title, string Description)> output)
    {
        var titleLength = body[3];
        if (4 + titleLength >= body.Length)
        {
            return;
        }

        var textLength = body[4 + titleLength];
        if (5 + titleLength + textLength > body.Length)
        {
            return;
        }

        output[Encoding.ASCII.GetString(body[..3])] = (DvbText.Decode(body.Slice(4, titleLength)), DvbText.Decode(body.Slice(5 + titleLength, textLength)));
    }

    private static void ReadExtended(ReadOnlySpan<byte> body, Dictionary<string, SortedDictionary<int, string>> output)
    {
        var itemsLength = body[4];
        if (5 + itemsLength >= body.Length)
        {
            return;
        }

        var textLength = body[5 + itemsLength];
        if (6 + itemsLength + textLength > body.Length)
        {
            return;
        }

        var language = Encoding.ASCII.GetString(body.Slice(1, 3));
        if (!output.TryGetValue(language, out var pieces))
        {
            pieces = [];
            output.Add(language, pieces);
        }

        pieces[body[0] >> 4] = DvbText.Decode(body.Slice(6 + itemsLength, textLength));
    }

    private static ushort Read16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));

    private static int Bcd(byte value) => (value & 15) > 9 || (value >> 4) > 9 ? -1 : ((value >> 4) * 10) + (value & 15);
}
