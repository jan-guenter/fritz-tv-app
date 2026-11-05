namespace FritzTV.Infrastructure.Recording;

public static class RecordingPriorityReader
{
    public static async ValueTask<int> ReadAsync(Stream source, Memory<byte> destination, Task priorityDrain, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(priorityDrain);
        cancellationToken.ThrowIfCancellationRequested();
        if (priorityDrain.IsCompleted)
        {
            throw PriorityLost(priorityDrain);
        }

        using var readLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<int> pending = source.ReadAsync(destination, readLifetime.Token).AsTask();
        _ = await Task.WhenAny(pending, priorityDrain).ConfigureAwait(false);
        if (priorityDrain.IsCompleted)
        {
            await readLifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                _ = await pending.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidDataException or ObjectDisposedException)
            {
                // Settle the read before its owning buffer can be disposed during reconnection.
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw PriorityLost(priorityDrain);
        }

        return await pending.ConfigureAwait(false);
    }

    private static IOException PriorityLost(Task priorityDrain) => new("The recording-priority tuner session ended; a recording-priority connection must be reacquired.",
        priorityDrain.IsCanceled ? new TaskCanceledException(priorityDrain) : priorityDrain.Exception?.GetBaseException());
}
