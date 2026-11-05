using System.Text;
using FritzTV.Infrastructure.Dvb;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class DvbTests
{
    [Fact]
    public void EitParsesProgrammeIdentityUtcAndDvbUtf8Text()
    {
        var device = Guid.NewGuid();
        var section = DvbFixtures.Eit();
        Assert.Equal(0u, MpegCrc32.Compute(section));
        var programme = Assert.Single(EitParser.Parse(device, section));
        Assert.Equal(device, programme.Service.DeviceId);
        Assert.Equal(10301, programme.Service.ServiceId);
        Assert.Equal(1101, programme.Service.TransportStreamId);
        Assert.Equal(1, programme.Service.OriginalNetworkId);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 20, 15, 0, TimeSpan.Zero), programme.Start);
        Assert.Equal(TimeSpan.FromMinutes(45), programme.Duration);
        Assert.Equal("Synthetic evening news", programme.Title);
        Assert.Equal("Original test programme: Grüße", programme.Description);
    }

    [Fact]
    public void ExtendedDescriptionsPreserveLabelledItemsAndOrderedTextContinuations()
    {
        var first = ExtendedDescriptor(0, "A con", [(byte)'C', (byte)'a', (byte)'s', (byte)'t'], [(byte)'A', (byte)'d', (byte)'a']);
        var second = ExtendedDescriptor(1, "tinuation.", [], []);
        var programme = Assert.Single(EitParser.Parse(Guid.NewGuid(), DvbFixtures.EitWithDescriptors(second, first)));
        Assert.Contains("Cast: Ada", programme.Description, StringComparison.Ordinal);
        Assert.Contains("A continuation.", programme.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("A con" + Environment.NewLine + "tinuation", programme.Description, StringComparison.Ordinal);
        first[7] = 250;
        programme = Assert.Single(EitParser.Parse(Guid.NewGuid(), DvbFixtures.EitWithDescriptors(first)));
        Assert.Equal("Original test programme: Grüße", programme.Description);
    }

    [Fact]
    public void EitRejectsCorruptionTruncationAndFutureVersion()
    {
        var section = DvbFixtures.Eit();
        Assert.Empty(EitParser.Parse(Guid.NewGuid(), section.AsSpan(0, section.Length - 1)));
        section[20] ^= 1;
        Assert.Empty(EitParser.Parse(Guid.NewGuid(), section));
    }

    [Fact]
    public void TextDecoderHandlesIso6937AccentsDvbEuroAndControls()
    {
        Assert.Equal("Café €\nNews", DvbText.Decode([(byte)'C', (byte)'a', (byte)'f', 0xc2, (byte)'e', (byte)' ', 0xa4, 0x8a, 0x86, (byte)'N', (byte)'e', (byte)'w', (byte)'s', 0x87]));
        Assert.Equal("Grüße", DvbText.Decode([0x10, 0, 1, (byte)'G', (byte)'r', 0xfc, 0xdf, (byte)'e']));
    }

    [Fact]
    public void AssemblerReassemblesFragmentedSectionsAndRejectsContinuityGaps()
    {
        var packets = DvbFixtures.Packetize(DvbFixtures.Pat(50), 0);
        var assembler = new PsiSectionAssembler();
        var received = new List<PsiSectionEventArgs>();
        assembler.SectionReceived += (_, section) => received.Add(section);
        foreach (var packet in packets)
        {
            assembler.Push(packet);
        }

        Assert.Single(received);
        Assert.Equal(5100, ProgrammeMapParser.ParsePat(received[0].Data.Span)[10301]);
        received.Clear();
        assembler = new PsiSectionAssembler();
        assembler.SectionReceived += (_, section) => received.Add(section);
        assembler.Push(packets[0]);
        packets[1][3] = 0x13;
        assembler.Push(packets[1]);
        Assert.Empty(received);
    }
    private static byte[] ExtendedDescriptor(byte number, string text, byte[] label, byte[] item)
    {
        var body = new List<byte> { (byte)((number << 4) | 1), (byte)'d', (byte)'e', (byte)'u', (byte)(label.Length == 0 ? 0 : label.Length + item.Length + 2) };
        if (label.Length > 0)
        {
            body.Add((byte)label.Length);
            body.AddRange(label);
            body.Add((byte)item.Length);
            body.AddRange(item);
        }

        var encoded = Encoding.ASCII.GetBytes(text);
        body.Add((byte)encoded.Length);
        body.AddRange(encoded);
        return new byte[] { 0x4e, checked((byte)body.Count) }.Concat(body).ToArray();
    }

}
