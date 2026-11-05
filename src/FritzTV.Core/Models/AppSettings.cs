namespace FritzTV.Core.Models;

public sealed record AppSettings
{
    public string Language { get; init; } = "system";

    public string Theme { get; init; } = "system";

    public bool StartAtLogin { get; init; }

    public string RecordingDirectory { get; init; } = DefaultRecordingDirectory();

    public string BufferDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FritzTV", "timeshift");

    public TimeSpan TimeshiftDuration { get; init; } = TimeSpan.FromHours(1);

    public long TimeshiftBytes { get; init; } = 10L * 1024 * 1024 * 1024;

    public long FreeSpaceReserveBytes { get; init; } = 1024L * 1024 * 1024;

    public TimeSpan GuideRefreshInterval { get; init; } = TimeSpan.FromHours(6);

    public int RtpFirstPort { get; init; } = 50000;

    public int RtpLastPort { get; init; } = 50127;
    private static string DefaultRecordingDirectory()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        return Path.Combine(string.IsNullOrEmpty(videos) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos") : videos, "FritzTV");
    }

}
