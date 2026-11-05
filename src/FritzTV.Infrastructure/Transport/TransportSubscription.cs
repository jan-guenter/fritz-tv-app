using System.Threading.Channels;

namespace FritzTV.Infrastructure.Transport;

public sealed class TransportSubscription(ChannelReader<ReadOnlyMemory<byte>> reader, Action unsubscribe) : IDisposable
{
    private bool disposed;

    public ChannelReader<ReadOnlyMemory<byte>> Reader { get; } = reader;

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            unsubscribe();
        }
    }
}
