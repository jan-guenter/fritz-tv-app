using FritzTV.Infrastructure.Dvb;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class ProgrammeClockTests
{
    [Fact]
    public void PcrWrapIsUnwrappedWithoutMovingPlaybackBackwards()
    {
        var clock = new ProgrammeClock();
        var start = new DateTimeOffset(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal(start, clock.Update((1L << 33) - 45000, start));
        Assert.Equal(start.AddSeconds(1), clock.Update(45000, start.AddSeconds(1)));
        Assert.Equal(start.AddSeconds(2), clock.Update(135000, start.AddSeconds(2)));
    }

    [Fact]
    public void PcrDiscontinuityStartsANewWallClockAnchor()
    {
        var clock = new ProgrammeClock();
        var start = new DateTimeOffset(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
        clock.Update(90000, start);
        Assert.Equal(start.AddMinutes(1), clock.Update(0, start.AddMinutes(1), discontinuity: true));
    }

    [Fact]
    public void CompleteMultiPacketPsiPrefixRoundTripsThroughAssembler()
    {
        var section = DvbFixtures.Pat(100);
        var packetized = PsiPacketizer.Packetize(0, section);
        var assembler = new PsiSectionAssembler();
        var received = new List<PsiSectionEventArgs>();
        assembler.SectionReceived += (_, value) => received.Add(value);
        for (var offset = 0; offset < packetized.Length; offset += 188)
        {
            assembler.Push(packetized.AsSpan(offset, 188));
        }

        Assert.Equal(section, Assert.Single(received).Data.ToArray());
    }
}
