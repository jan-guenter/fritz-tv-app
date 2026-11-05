using System.Text;

namespace FritzTV.Infrastructure.Dvb;

public static class DvbText
{
    private static readonly Encoding Iso6937;

    static DvbText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Iso6937 = Encoding.GetEncoding(20269);
    }

    public static string Decode(ReadOnlySpan<byte> data) => Decode(data, trim: true);

    internal static string Decode(ReadOnlySpan<byte> data, bool trim)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        if (data[0] >= 0x20)
        {
            return DecodeIso6937(data, trim);
        }

        var prefix = 1;
        Encoding encoding;
        try
        {
            if (data[0] is >= 1 and <= 11 && data[0] != 8)
            {
                encoding = Encoding.GetEncoding("iso-8859-" + (data[0] + 4).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (data[0] == 0x10 && data.Length >= 3 && data[1] == 0 && data[2] is >= 1 and <= 15 && data[2] != 12)
            {
                prefix = 3;
                encoding = Encoding.GetEncoding("iso-8859-" + data[2].ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                encoding = data[0] switch
                {
                    0x11 => Encoding.BigEndianUnicode,
                    0x12 => Encoding.GetEncoding(949),
                    0x13 => Encoding.GetEncoding(936),
                    0x14 => Encoding.GetEncoding(950),
                    0x15 => Encoding.UTF8,
                    _ => throw new InvalidDataException("Unsupported DVB text encoding selector."),
                };
            }
        }
        catch (ArgumentException)
        {
            return "\uFFFD";
        }
        catch (InvalidDataException)
        {
            return "\uFFFD";
        }

        return Clean(encoding.GetString(data[prefix..]), trim);
    }

    private static string DecodeIso6937(ReadOnlySpan<byte> data, bool trim)
    {
        var output = new StringBuilder();
        for (var index = 0; index < data.Length; index++)
        {
            var value = data[index];
            var accent = value switch
            {
                0xc1 => '\u0300',
                0xc2 => '\u0301',
                0xc3 => '\u0302',
                0xc4 => '\u0303',
                0xc5 => '\u0304',
                0xc6 => '\u0306',
                0xc7 => '\u0307',
                0xc8 => '\u0308',
                0xca => '\u030a',
                0xcb => '\u0327',
                0xcd => '\u030b',
                0xce => '\u0328',
                0xcf => '\u030c',
                _ => '\0',
            };
            if (accent != '\0' && index + 1 < data.Length && data[index + 1] != 0x20)
            {
                output.Append(Iso6937.GetString(data.Slice(++index, 1))).Append(accent);
            }
            else if (value == 0xa4)
            {
                output.Append('€');
            }
            else if (value == 0x8a)
            {
                output.Append('\n');
            }
            else if (value is < 0x80 or >= 0xa0)
            {
                output.Append(Iso6937.GetString(data.Slice(index, 1)));
            }
        }

        return Clean(output.ToString().Normalize(NormalizationForm.FormC), trim);
    }

    private static string Clean(string value, bool trim)
    {
        var cleaned = string.Concat(value.Select(character => character == '\u008a' ? '\n' : character).Where(character => !char.IsControl(character) || character == '\n'));
        return trim ? cleaned.Trim() : cleaned;
    }
}
