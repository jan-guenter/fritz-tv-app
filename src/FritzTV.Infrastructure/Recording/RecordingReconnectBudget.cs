namespace FritzTV.Infrastructure.Recording;

internal sealed class RecordingReconnectBudget(TimeProvider clock)
{
    private static readonly TimeSpan HealthyInterval = TimeSpan.FromSeconds(30);
    private int failures;
    private long? healthySince;

    public bool CanRetry => failures < 3;

    public TimeSpan Failed()
    {
        healthySince = null;
        return TimeSpan.FromSeconds(1 << failures++);
    }

    public void Delivered()
    {
        healthySince ??= clock.GetTimestamp();
        if (clock.GetElapsedTime(healthySince.Value) >= HealthyInterval)
        {
            failures = 0;
        }
    }
}
