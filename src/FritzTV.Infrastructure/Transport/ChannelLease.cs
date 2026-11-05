using System.Runtime.CompilerServices;
using FritzTV.Core.Models;

namespace FritzTV.Infrastructure.Transport;

public sealed class ChannelLease(TvChannel channel, SatIpSession session, TransportSubscription subscription, IReadOnlySet<int> pids, Func<IReadOnlySet<int>, CancellationToken, Task> update, Func<Task> release) : IAsyncDisposable
{
    private int disposed;
    private IReadOnlySet<int> currentPids = pids;
    private readonly SemaphoreSlim updateGate = new(1, 1);

    public TvChannel Channel { get; } = channel;

    public SatIpSession Session { get; } = session;

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var batch in subscription.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var filtered = SatIpTuning.Filter(batch, Volatile.Read(ref currentPids));
            if (!filtered.IsEmpty)
            {
                yield return filtered;
            }
        }
    }

    public async Task UpdatePidsAsync(IEnumerable<int> requested, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var next = requested.ToHashSet();
        next.UnionWith([0, 16, 17, 18, 20]);
        if (next.Any(pid => pid is < 0 or > 8191))
        {
            throw new ArgumentOutOfRangeException(nameof(requested));
        }

        await updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (!currentPids.SetEquals(next))
            {
                await update(next, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref currentPids, next);
            }
        }
        finally
        {
            updateGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            subscription.Dispose();
            await updateGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await release().ConfigureAwait(false);
            }
            finally
            {
                updateGate.Release();
            }
        }
    }
}
