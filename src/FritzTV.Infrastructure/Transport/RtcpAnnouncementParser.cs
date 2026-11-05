using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FritzTV.Core.Models;

namespace FritzTV.Infrastructure.Transport;

public static class RtcpAnnouncementParser
{
    public static bool TryParse(ReadOnlySpan<byte> datagram, out ReceptionStatus? status)
    {
        status = null;
        if (datagram.IsEmpty)
        {
            return false;
        }

        while (!datagram.IsEmpty)
        {
            if (datagram.Length < 4 || datagram[0] >> 6 != 2)
            {
                return false;
            }

            var length = (BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]) + 1) * 4;
            if (length > datagram.Length || length < 4)
            {
                return false;
            }

            var packet = datagram[..length];
            if ((packet[0] & 0x20) != 0)
            {
                var padding = packet[^1];
                if (length != datagram.Length || padding == 0 || padding > length - 4)
                {
                    return false;
                }

                packet = packet[..^padding];
            }

            if (packet[1] == 204 && !TryReadApplication(packet, ref status))
            {
                return false;
            }

            datagram = datagram[length..];
        }

        return true;
    }

    private static bool TryReadApplication(ReadOnlySpan<byte> packet, ref ReceptionStatus? status)
    {
        if (packet.Length < 12)
        {
            return false;
        }

        if (!packet.Slice(8, 4).SequenceEqual("SES1"u8))
        {
            return true;
        }

        if (packet.Length < 16)
        {
            return false;
        }

        var version = BinaryPrimitives.ReadUInt16BigEndian(packet[12..]);
        var stringLength = BinaryPrimitives.ReadUInt16BigEndian(packet[14..]);
        if (stringLength > 4096 || stringLength > packet.Length - 16)
        {
            return false;
        }

        if (version == 0)
        {
            var text = packet.Slice(16, stringLength);
            if (text.ContainsAnyExceptInRange((byte)0x20, (byte)0x7e))
            {
                return false;
            }

            status = ParseParameters(Encoding.ASCII.GetString(text)) ?? status;
        }

        return true;
    }

    private static ReceptionStatus? ParseParameters(string parameters)
    {
        var tuner = parameters.Split(';', StringSplitOptions.TrimEntries).FirstOrDefault(value => value.StartsWith("tuner=", StringComparison.Ordinal));
        if (tuner is null)
        {
            return null;
        }

        var fields = tuner[6..].Split(',');
        if (fields.Length < 4 || !Number(fields[0], 65535, out var frontend) || !Number(fields[1], 255, out var level) || !Number(fields[2], 1, out var hasLock) || !Number(fields[3], 15, out var quality))
        {
            return null;
        }

        decimal? frequency = fields.Length > 4 && decimal.TryParse(fields[4], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : null;
        return new ReceptionStatus(frontend, level, hasLock != 0, quality, frequency, parameters);
    }

    private static bool Number(string text, int maximum, out int value) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0 && value <= maximum;
}
