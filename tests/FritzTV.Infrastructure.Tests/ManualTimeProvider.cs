namespace FritzTV.Infrastructure.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset now = new(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
    private long timestamp;

    public override DateTimeOffset GetUtcNow() => now;

    public override long GetTimestamp() => timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan duration)
    {
        now += duration;
        timestamp += duration.Ticks;
    }
}
