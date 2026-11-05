namespace FritzTV.Infrastructure.Transport;

public sealed class TransportDataEventArgs(ReadOnlyMemory<byte> data) : EventArgs
{
    public ReadOnlyMemory<byte> Data { get; } = data;
}
