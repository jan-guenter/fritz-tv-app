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
                result.Add(new Programme(identity, Read16(data, 0), start, duration, text.Title, text.Description, text.Language, section[0], (byte)((section[5] >> 1) & 31), section[0] is 0x4e or >= 0x50 and <= 0x5f));
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
        var extended = new Dictionary<string, SortedDictionary<int, (string Items, byte[] Text)>>(StringComparer.Ordinal);
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
        var extra = string.Empty;
        if (extended.TryGetValue(language, out var pieces))
        {
            var labels = string.Join(Environment.NewLine, pieces.Values.Select(item => item.Items).Where(item => !string.IsNullOrWhiteSpace(item)));
            var continuation = DecodeExtendedText(pieces.Values.Select(item => item.Text).ToArray());
            extra = string.Join(Environment.NewLine, new[] { labels, continuation }.Where(item => !string.IsNullOrWhiteSpace(item)));
        }
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

    private static void ReadExtended(ReadOnlySpan<byte> body, Dictionary<string, SortedDictionary<int, (string Items, byte[] Text)>> output)
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

        var items = ReadExtendedItems(body.Slice(5, itemsLength));
        if (items is null || (body[0] >> 4) > (body[0] & 15))
        {
            return;
        }

        var language = Encoding.ASCII.GetString(body.Slice(1, 3));
        if (!output.TryGetValue(language, out var pieces))
        {
            pieces = [];
            output.Add(language, pieces);
        }

        pieces[body[0] >> 4] = (items, body.Slice(6 + itemsLength, textLength).ToArray());
    }

    private static string DecodeExtendedText(byte[][] segments)
    {
        var result = new StringBuilder();
        using var joined = new MemoryStream();
        byte[]? selector = null;
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                continue;
            }

            var text = segment.AsSpan();
            var prefixLength = text[0] >= 0x20 ? 0 : text[0] == 0x10 ? Math.Min(3, text.Length) : 1;
            var prefix = text[..prefixLength];
            if (selector is not null && !prefix.SequenceEqual(selector))
            {
                result.Append(DvbText.Decode(joined.GetBuffer().AsSpan(0, checked((int)joined.Length)), trim: false));
                joined.SetLength(0);
                selector = null;
            }

            if (selector is null)
            {
                selector = prefix.ToArray();
                joined.Write(text);
            }
            else
            {
                // Compatible fragments may split a multibyte character across descriptor fields.
                joined.Write(text[prefixLength..]);
            }
        }

        if (joined.Length > 0)
        {
            result.Append(DvbText.Decode(joined.GetBuffer().AsSpan(0, checked((int)joined.Length)), trim: false));
        }

        return result.ToString().Trim();
    }

    private static string? ReadExtendedItems(ReadOnlySpan<byte> items)
    {
        var descriptions = new List<string>();
        while (!items.IsEmpty)
        {
            var labelLength = items[0];
            if (labelLength + 1 >= items.Length)
            {
                return null;
            }

            var textLength = items[labelLength + 1];
            if (labelLength + textLength + 2 > items.Length)
            {
                return null;
            }

            var label = DvbText.Decode(items.Slice(1, labelLength));
            var text = DvbText.Decode(items.Slice(labelLength + 2, textLength));
            descriptions.Add(string.IsNullOrWhiteSpace(label) ? text : label + ": " + text);
            items = items[(labelLength + textLength + 2)..];
        }

        return string.Join(Environment.NewLine, descriptions);
    }

    private static ushort Read16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));

    private static int Bcd(byte value) => (value & 15) > 9 || (value >> 4) > 9 ? -1 : ((value >> 4) * 10) + (value & 15);
}
