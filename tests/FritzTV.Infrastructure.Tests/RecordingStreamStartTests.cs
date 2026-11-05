using System.Buffers.Binary;
using FritzTV.Core.Models;
using FritzTV.Infrastructure.Dvb;
using FritzTV.Infrastructure.Recording;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class RecordingStreamStartTests
{
    [Fact]
    public void RadioStartsAtItsAudioPesWhenPcrUsesASeparateAdaptationOnlyPid()
    {
        var start = new RecordingStreamStart(ChannelKind.Radio, 42);
        var headers = Batch(PsiPacketizer.Packetize(0, DvbFixtures.PatService(42, 99, 0, 0)), PsiPacketizer.Packetize(99, Pmt()), Packet(200, adaptationOnly: true));
        Assert.Empty(start.Push(headers));
        var output = start.Push(Packet(100));
        Assert.Equal(3, output.Count);
        Assert.Equal(42, start.ServiceId);
        Assert.Equal(100, ((output[^1].Span[1] & 31) << 8) | output[^1].Span[2]);
    }

    [Fact]
    public void RecordingWaitsForAllPatSectionsAndPrependsThemWithContinuousCounters()
    {
        var start = new RecordingStreamStart(ChannelKind.Radio, 42);
        var first = Batch(PsiPacketizer.Packetize(0, DvbFixtures.PatService(42, 99, 0, 1)), PsiPacketizer.Packetize(99, Pmt()), Packet(100));
        Assert.Empty(start.Push(first));
        var second = Batch(PsiPacketizer.Packetize(0, DvbFixtures.PatService(41, 98, 1, 1), 1), Packet(100));
        var output = start.Push(second);
        Assert.Equal(4, output.Count);
        Assert.Equal(0, output[0].Span[3] & 15);
        Assert.Equal(1, output[1].Span[3] & 15);
        var assembler = new PsiSectionAssembler();
        var services = new List<ushort>();
        assembler.SectionReceived += (_, args) => services.AddRange(ProgrammeMapParser.ParsePat(args.Data.Span).Keys);
        assembler.Push(output[0].Span);
        assembler.Push(output[1].Span);
        Assert.Equal(new ushort[] { 42, 41 }, services);
    }

    [Fact]
    public void StartedCaptureContinuesTrackingPmtChangesWithoutRepeatedMapUpdates()
    {
        var start = new RecordingStreamStart(ChannelKind.Radio, 42);
        Assert.NotEmpty(start.Push(Batch(PsiPacketizer.Packetize(0, DvbFixtures.PatService(42, 99, 0, 0)), PsiPacketizer.Packetize(99, Pmt()), Packet(100))));
        var original = start.Programme;
        _ = start.Push(PsiPacketizer.Packetize(99, Pmt(), 1));
        Assert.Same(original, start.Programme);
        var changed = PsiPacketizer.Packetize(99, Pmt(audioPid: 101, version: 1), 2);
        Assert.Single(start.Push(changed));
        Assert.NotSame(original, start.Programme);
        Assert.Equal(101, Assert.Single(start.Programme!.Streams).Pid);
    }

    [Fact]
    public void PatPidMoveKeepsOldElementaryStreamsUntilTheReplacementPmtArrives()
    {
        var start = new RecordingStreamStart(ChannelKind.Radio, 42);
        Assert.NotEmpty(start.Push(Batch(PsiPacketizer.Packetize(0, DvbFixtures.PatService(42, 99, 0, 0)), PsiPacketizer.Packetize(99, Pmt()), Packet(100))));
        _ = start.Push(PsiPacketizer.Packetize(0, DvbFixtures.PatService(42, 98, 0, 0, version: 1), 1));
        Assert.Contains(99, start.RequiredPids!);
        Assert.Contains(98, start.RequiredPids!);
        Assert.Contains(100, start.RequiredPids!);
        Assert.Equal(99, start.Programme!.PmtPid);
        _ = start.Push(PsiPacketizer.Packetize(98, Pmt()));
        Assert.Equal(98, start.Programme.PmtPid);
        Assert.DoesNotContain(99, start.RequiredPids!);
        Assert.Contains(98, start.RequiredPids!);
        Assert.Contains(100, start.RequiredPids!);
    }

    private static byte[] Pmt(int audioPid = 100, int version = 0)
    {
        byte[] data = [2, 0xb0, 18, 0, 42, (byte)(0xc1 | (version << 1)), 0, 0, 0xe0, 200, 0xf0, 0, 0x11, 0xe0, (byte)audioPid, 0xf0, 0];
        var result = new byte[data.Length + 4];
        data.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(data.Length), MpegCrc32.Compute(data));
        return result;
    }

    private static byte[] Packet(int pid, bool adaptationOnly = false)
    {
        var packet = Enumerable.Repeat((byte)0xff, 188).ToArray();
        packet[0] = 0x47;
        packet[1] = (byte)(0x40 | (pid >> 8));
        packet[2] = (byte)pid;
        packet[3] = (byte)(adaptationOnly ? 0x20 : 0x10);
        if (adaptationOnly)
        {
            packet[4] = 183;
            packet[5] = 0;
        }
        else
        {
            packet[4] = 0;
            packet[5] = 0;
            packet[6] = 1;
            packet[7] = 0xc0;
        }

        return packet;
    }

    private static byte[] Batch(params byte[][] packets) => packets.SelectMany(packet => packet).ToArray();
}
