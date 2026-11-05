namespace FritzTV.Desktop.Services;

public sealed record PlaybackSnapshot(
    bool IsLive,
    bool IsPaused,
    DateTimeOffset Earliest,
    DateTimeOffset Live,
    DateTimeOffset Position,
    bool ResumePointExpired,
    string? Failure)
{
    public static PlaybackSnapshot ForRecording(bool paused, long elapsedMilliseconds, long lengthMilliseconds)
    {
        long length = Math.Max(0, lengthMilliseconds);
        long elapsed = length > 0 ? Math.Clamp(elapsedMilliseconds, 0, length) : Math.Max(0, elapsedMilliseconds);
        long extent = Math.Max(length, elapsed);
        return new PlaybackSnapshot(false, paused, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMilliseconds(extent),
            DateTimeOffset.UnixEpoch.AddMilliseconds(elapsed), false, null);
    }
}
