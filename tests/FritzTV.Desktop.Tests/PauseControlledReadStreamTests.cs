using FritzTV.Desktop.Services;
using Xunit;

namespace FritzTV.Desktop.Tests;

public sealed class PauseControlledReadStreamTests
{
    [Fact]
    public async Task CancellingAPausedNativeReaderUnblocksShutdown()
    {
        using var source = new MemoryStream([1, 2, 3]);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var reader = new PauseControlledReadStream(source, lifetime.Token);
        reader.SetPaused(paused: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> pending = Task.Run(() =>
        {
            entered.SetResult();
            return reader.Read(new byte[188], 0, 188);
        }, TestContext.Current.CancellationToken);
        await entered.Task.ConfigureAwait(true);
        await lifetime.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(0, source.Position);
    }

    [Fact]
    public void ResumingPreservesUnreadTransportBytes()
    {
        using var source = new MemoryStream([1, 2, 3]);
        using var reader = new PauseControlledReadStream(source, TestContext.Current.CancellationToken);
        reader.SetPaused(paused: true);
        reader.SetPaused(paused: false);
        var bytes = new byte[3];
        Assert.Equal(3, reader.Read(bytes, 0, bytes.Length));
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    }
}
