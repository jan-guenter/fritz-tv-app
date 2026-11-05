namespace FritzTV.Infrastructure.Diagnostics;

public sealed record RotatingFileLoggerOptions
{
    public required string DirectoryPath { get; init; }

    public long MaxFileBytes { get; init; } = 4 * 1024 * 1024;

    public int RetainedFileCount { get; init; } = 5;

    public int QueueCapacity { get; init; } = 1024;
}
