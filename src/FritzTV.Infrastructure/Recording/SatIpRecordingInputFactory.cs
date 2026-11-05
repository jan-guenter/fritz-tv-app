using System.Runtime.CompilerServices;
using FritzTV.Core.Models;
using FritzTV.Infrastructure.Persistence;
using FritzTV.Infrastructure.Playback;
using FritzTV.Infrastructure.Transport;

namespace FritzTV.Infrastructure.Recording;

public sealed class SatIpRecordingInputFactory(AppRepository repository, SatIpSessionManager sessions) : IRecordingInputFactory
{
    public async Task<IRecordingInput> OpenAsync(TvChannel channel, BufferedPlayback? playback = null, DateTimeOffset? from = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var devices = await repository.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        var device = devices.FirstOrDefault(item => item.Id == channel.DeviceId) ?? throw new InvalidOperationException("The recording device is no longer configured.");
        IAsyncDisposable? retention = null;
        ChannelLease? lease = null;
        try
        {
            if (playback is not null && string.Equals(playback.Channel.Id, channel.Id, StringComparison.Ordinal))
            {
                retention = playback.Retain();
            }

            lease = await AcquireLeaseAsync(device, channel, cancellationToken).ConfigureAwait(false);
            if (retention is not null && playback is not null)
            {
                var reader = playback.Buffer.OpenPinnedReader(from ?? playback.Buffer.Range?.Live, CancellationToken.None);
                return new BufferedInput(lease, playback, reader, retention);
            }

            return new DirectInput(lease);
        }
        catch
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }

            if (retention is not null)
            {
                await retention.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task<ChannelLease> AcquireLeaseAsync(SatIpDevice device, TvChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            return await sessions.AcquireAsync(device, channel, TunerPriority.Recording, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Timed out acquiring the recording tuner.", exception);
        }
        catch (System.Net.Sockets.SocketException exception)
        {
            throw new IOException("The recording tuner connection failed: " + exception.Message, exception);
        }
    }

    private sealed class DirectInput : IRecordingInput
    {
        private readonly ChannelLease lease;
        private readonly long initialLost;
        private readonly RecordingStreamStart start;

        public DirectInput(ChannelLease lease)
        {
            this.lease = lease;
            initialLost = lease.Session.LostPackets;
            start = new RecordingStreamStart(lease.Channel.Kind, lease.Channel.ServiceId);
        }

        public int? ServiceId => start.ServiceId;

        public long LostPackets => Math.Max(0, lease.Session.LostPackets - initialLost);

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            IReadOnlySet<int>? applied = null;
            await foreach (var batch in lease.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var parts = start.Push(batch);
                if (start.RequiredPids is { } requested && !ReferenceEquals(applied, requested))
                {
                    await lease.UpdatePidsAsync(requested, cancellationToken).ConfigureAwait(false);
                    applied = requested;
                }

                foreach (var part in parts)
                {
                    yield return part;
                }
            }
        }

        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }

    private sealed class BufferedInput : IRecordingInput
    {
        private readonly ChannelLease lease;
        private readonly BufferedPlayback playback;
        private readonly Stream reader;
        private readonly IAsyncDisposable retention;
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task drain;
        private readonly long initialLost;

        public BufferedInput(ChannelLease lease, BufferedPlayback playback, Stream reader, IAsyncDisposable retention)
        {
            this.lease = lease;
            this.playback = playback;
            this.reader = reader;
            this.retention = retention;
            initialLost = lease.Session.LostPackets;
            drain = DrainPriorityLeaseAsync(lifetime.Token);
        }

        public int? ServiceId => playback.Programme?.ServiceId ?? lease.Channel.ServiceId;

        public long LostPackets => Math.Max(0, lease.Session.LostPackets - initialLost);

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var buffer = new byte[188 * 256];
            while (true)
            {
                var count = await RecordingPriorityReader.ReadAsync(reader, buffer, drain, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    yield break;
                }

                yield return buffer.AsMemory(0, count);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                await drain.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or System.Threading.Channels.ChannelClosedException)
            {
                // Reception failure is also visible to the buffered transport reader.
            }
            finally
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                await lease.DisposeAsync().ConfigureAwait(false);
                await retention.DisposeAsync().ConfigureAwait(false);
                lifetime.Dispose();
            }
        }

        private async Task DrainPriorityLeaseAsync(CancellationToken cancellationToken)
        {
            await foreach (var _ in lease.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // The growing buffer is the single capture source; this lease protects tuner priority.
            }
        }
    }
}
