namespace FritzTV.Core.Models;

public sealed record GuideStatus(bool IsScanning, int CompletedFrequencies, int TotalFrequencies, DateTimeOffset? LastRefreshUtc, int ProgrammeCount, DateTimeOffset? CoverageUntil, string? LastError);
