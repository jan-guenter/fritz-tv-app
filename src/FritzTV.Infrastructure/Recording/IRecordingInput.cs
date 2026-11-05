namespace FritzTV.Infrastructure.Recording;

public interface IRecordingInput : IAsyncDisposable
{
    int? ServiceId { get; }

    long LostPackets { get; }

    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken cancellationToken = default);
}
