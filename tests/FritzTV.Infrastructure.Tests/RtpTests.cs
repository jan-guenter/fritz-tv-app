using System.Buffers.Binary;
using FritzTV.Infrastructure.Transport;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class RtpTests
{
    [Fact]
    public void ParserAcceptsTransportPayloadAndRejectsTruncatedExtension()
    {
        var datagram = new byte[200];
        datagram[0] = 0x80;
        datagram[1] = 33;
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), 65535);
        datagram[12] = 0x47;
        Assert.True(RtpPacket.TryParse(datagram, out var packet));
        Assert.Equal(65535, packet!.Sequence);
        Assert.Equal(188, packet.Payload.Length);
        datagram[0] |= 0x10;
        datagram[14] = 0xff;
        datagram[15] = 0xff;
        Assert.False(RtpPacket.TryParse(datagram, out _));
    }

    [Fact]
    public void ReorderHandlesSequenceWrapAndDelayedPackets()
    {
        var buffer = new RtpReorderBuffer();
        Assert.Single(buffer.Push(Packet(65534)));
        Assert.Empty(buffer.Push(Packet(0)));
        var released = buffer.Push(Packet(65535));
        Assert.Equal(new ushort[] { 65535, 0 }, released.Select(packet => packet.Sequence));
        Assert.Equal(0, buffer.LostPackets);
        Assert.Empty(buffer.Push(Packet(65535)));
        Assert.Equal(1, buffer.DuplicatePackets);
    }

    [Fact]
    public void ReorderBoundsMissingPacketsAndResetsForNewSource()
    {
        var buffer = new RtpReorderBuffer(capacity: 1);
        buffer.Push(Packet(10));
        Assert.Empty(buffer.Push(Packet(12)));
        Assert.Equal(2, buffer.Push(Packet(13)).Count);
        Assert.Equal(1, buffer.LostPackets);
        Assert.Single(buffer.Push(new RtpPacket(1, 0, 999, new byte[188])));
    }

    private static RtpPacket Packet(ushort sequence) => new(sequence, 0, 1, new byte[188]);
}
