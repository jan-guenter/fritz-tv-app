using FritzTV.Desktop.Services;
using Xunit;

namespace FritzTV.Desktop.Tests;

public sealed class PlaybackSnapshotTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TimeBeforeDurationKeepsTheCursorForDecoderRestart(long unknownLength)
    {
        PlaybackSnapshot early = PlaybackSnapshot.ForRecording(true, 12000, unknownLength);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(12), early.Position);
        Assert.True(early.IsPaused);
        Assert.True(early.Live >= early.Position);
        long retained = (long)(early.Position - DateTimeOffset.UnixEpoch).TotalMilliseconds;
        PlaybackSnapshot restarted = PlaybackSnapshot.ForRecording(false, retained, 90000);
        Assert.Equal(early.Position, restarted.Position);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(90), restarted.Live);
    }

    [Theory]
    [InlineData(-1000, 90000, 0)]
    [InlineData(100000, 90000, 90000)]
    public void KnownDurationClampsOutOfRangeNativeTimes(long elapsed, long duration, long expected)
    {
        PlaybackSnapshot snapshot = PlaybackSnapshot.ForRecording(false, elapsed, duration);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMilliseconds(expected), snapshot.Position);
    }
}
