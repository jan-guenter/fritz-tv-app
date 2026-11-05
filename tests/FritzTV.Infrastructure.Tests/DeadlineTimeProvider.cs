namespace FritzTV.Infrastructure.Tests;

internal sealed class DeadlineTimeProvider : TimeProvider
{
    private readonly Lock gate = new();
    private readonly List<DeadlineTimer> timers = [];
    private long timestamp;

    public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (gate)
        {
            return timestamp;
        }
    }

    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 5, 20, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new DeadlineTimer(this, callback, state);
        lock (gate)
        {
            timers.Add(timer);
            timer.Change(dueTime, period);
        }

        TimerCreated.TrySetResult();
        return timer;
    }

    public void Advance(TimeSpan duration)
    {
        var callbacks = new List<(TimerCallback Callback, object? State)>();
        lock (gate)
        {
            timestamp += duration.Ticks;
            foreach (var timer in timers)
            {
                if (timer.Due is { } due && due <= timestamp)
                {
                    timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? null : timestamp + timer.Period.Ticks;
                    callbacks.Add((timer.Callback, timer.State));
                }
            }
        }

        foreach (var callback in callbacks)
        {
            callback.Callback(callback.State);
        }
    }

    private sealed class DeadlineTimer(DeadlineTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private bool disposed;

        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public long? Due { get; set; }
        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                if (disposed)
                {
                    return false;
                }

                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.timestamp + dueTime.Ticks;
                Period = period;
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner.gate)
            {
                disposed = true;
                owner.timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
