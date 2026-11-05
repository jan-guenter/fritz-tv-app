namespace FritzTV.Desktop.Services;

public sealed record PlaybackSnapshot(
    bool IsLive,
    bool IsPaused,
    DateTimeOffset Earliest,
    DateTimeOffset Live,
    DateTimeOffset Position,
    bool ResumePointExpired,
    string? Failure);
