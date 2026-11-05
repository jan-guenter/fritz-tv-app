using System.Buffers.Binary;
using System.Text;
using FritzTV.Infrastructure.Transport;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class RtcpTests
{
    [Fact]
    public void CableAnnouncementExposesSignalAndValidatesDeclaredLengths()
    {
        var packet = Announcement("ver=1.2;tuner=1,240,1,7,444.00,8,dvbc,256qam,6900,,,,0;pids=0,16,56");
        Assert.True(RtcpAnnouncementParser.TryParse(packet, out var status));
        Assert.NotNull(status);
        Assert.Equal(1, status.Frontend);
        Assert.Equal(240, status.SignalLevel);
        Assert.True(status.HasLock);
        Assert.Equal(7, status.SignalQuality);
        Assert.Equal(444m, status.FrequencyMhz);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(14), 65535);
        Assert.False(RtcpAnnouncementParser.TryParse(packet, out _));
    }

    [Fact]
    public void CompoundPacketSkipsSenderReportsAndRejectsTruncatedTrailingPacket()
    {
        var senderReport = new byte[28];
        senderReport[0] = 0x80;
        senderReport[1] = 200;
        BinaryPrimitives.WriteUInt16BigEndian(senderReport.AsSpan(2), 6);
        var announcement = Announcement("ver=1.2;tuner=0,0,0,0,444,8,dvbc;pids=none");
        var compound = senderReport.Concat(announcement).ToArray();
        Assert.True(RtcpAnnouncementParser.TryParse(compound, out var status));
        Assert.NotNull(status);
        Assert.False(status.HasLock);
        Assert.False(RtcpAnnouncementParser.TryParse(compound.AsSpan(0, compound.Length - 1), out _));
        Assert.False(RtcpAnnouncementParser.TryParse([], out _));
    }

    [Fact]
    public void MalformedOptionalSignalFieldsDoNotInvalidateTransportAnnouncement()
    {
        var packet = Announcement("ver=1.2;tuner=1,9999,1,7,444;pids=0");
        Assert.True(RtcpAnnouncementParser.TryParse(packet, out var status));
        Assert.Null(status);
        packet[12] = 1;
        Assert.True(RtcpAnnouncementParser.TryParse(packet, out status));
        Assert.Null(status);
    }

    private static byte[] Announcement(string parameters)
    {
        var text = Encoding.ASCII.GetBytes(parameters);
        var packet = new byte[(16 + text.Length + 3) / 4 * 4];
        packet[0] = 0x80;
        packet[1] = 204;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), checked((ushort)(packet.Length / 4 - 1)));
        "SES1"u8.CopyTo(packet.AsSpan(8));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(14), checked((ushort)text.Length));
        text.CopyTo(packet, 16);
        return packet;
    }
}
