using FritzTV.Infrastructure.Recording;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class RecordingPriorityReaderTests
{
    [Fact]
    public async Task FailedPriorityLeaseRejectsReadableBacklogBeforeItCanContinueAsViewing()
    {
        using var backlog = new MemoryStream([1, 2, 3]);
        var failed = Task.FromException(new IOException("Synthetic priority lease failure."));
        var failure = await Assert.ThrowsAsync<IOException>(async () => await RecordingPriorityReader.ReadAsync(backlog, new byte[3], failed, TestContext.Current.CancellationToken).ConfigureAwait(false));
        Assert.Contains("recording-priority", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, backlog.Position);
    }

    [Fact]
    public async Task PriorityFailureCancelsAndSettlesAnAlreadyPendingBufferRead()
    {
        using var pending = new PendingStream();
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> reading = RecordingPriorityReader.ReadAsync(pending, new byte[188], drain.Task, TestContext.Current.CancellationToken).AsTask();
        await pending.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        drain.SetException(new IOException("Synthetic priority lease failure."));
        await Assert.ThrowsAsync<IOException>(async () => await reading.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(false));
        Assert.True(pending.Settled);
    }

    private sealed class PendingStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Settled { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return 0;
            }
            finally
            {
                Settled = true;
            }
        }
    }
}
