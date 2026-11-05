using System.Runtime.CompilerServices;
using FritzTV.Core.Models;

namespace FritzTV.Infrastructure.Transport;

public sealed class ChannelLease(TvChannel channel, SatIpSession session, TransportSubscription subscription, IReadOnlySet<int> pids, Func<Task> release) : IAsyncDisposable
{
    private int disposed;

    public TvChannel Channel { get; } = channel;

    public SatIpSession Session { get; } = session;

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var batch in subscription.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var filtered = SatIpTuning.Filter(batch, pids);
            if (!filtered.IsEmpty)
            {
                yield return filtered;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            subscription.Dispose();
            await release().ConfigureAwait(false);
        }
    }
}
