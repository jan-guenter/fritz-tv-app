using System.Runtime.CompilerServices;
using FritzTV.Core.Models;
using FritzTV.Infrastructure.Dvb;
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
        private readonly PsiSectionAssembler sections = new();
        private readonly HashSet<int> pmtPids = [];
        private ProgrammeMap? programme;
        private byte[]? pat;
        private byte[]? pmt;

        public DirectInput(ChannelLease lease)
        {
            this.lease = lease;
            initialLost = lease.Session.LostPackets;
            ServiceId = lease.Channel.ServiceId;
            sections.SectionReceived += OnSectionReceived;
        }

        public int? ServiceId { get; private set; }

        public long LostPackets => Math.Max(0, lease.Session.LostPackets - initialLost);

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var started = false;
            await foreach (var batch in lease.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Inspect(batch);
                if (!started)
                {
                    var offset = FindEntryPoint(batch);
                    if (offset is null || pat is null || pmt is null || programme is null)
                    {
                        continue;
                    }

                    yield return PsiPacketizer.Packetize(0, pat);
                    yield return PsiPacketizer.Packetize(programme.PmtPid, pmt);
                    started = true;
                    yield return batch[offset.Value..];
                }
                else
                {
                    yield return batch;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            sections.SectionReceived -= OnSectionReceived;
            await lease.DisposeAsync().ConfigureAwait(false);
        }

        private void Inspect(ReadOnlyMemory<byte> batch)
        {
            for (var offset = 0; offset + 188 <= batch.Length; offset += 188)
            {
                var packet = batch.Span.Slice(offset, 188);
                if (TsPacket.TryParse(packet, out var ts) && (ts.Pid == 0 || pmtPids.Contains(ts.Pid)))
                {
                    sections.Push(packet);
                }
            }
        }

        private int? FindEntryPoint(ReadOnlyMemory<byte> batch)
        {
            if (programme is null)
            {
                return null;
            }

            var video = programme.Streams.FirstOrDefault(item => item.StreamType is 0x01 or 0x02 or 0x10 or 0x1b or 0x24)?.Pid;
            for (var offset = 0; offset + 188 <= batch.Length; offset += 188)
            {
                if (TsPacket.TryParse(batch.Span.Slice(offset, 188), out var packet) && packet.StartsPayload
                    && (lease.Channel.Kind == ChannelKind.Radio ? packet.Pid == programme.PcrPid : packet.Pid == video && packet.RandomAccess))
                {
                    return offset;
                }
            }

            return null;
        }

        private void OnSectionReceived(object? sender, PsiSectionEventArgs section)
        {
            if (section.Data.Span[0] == 0)
            {
                pat = section.Data.ToArray();
                foreach (var pair in ProgrammeMapParser.ParsePat(section.Data.Span))
                {
                    pmtPids.Add(pair.Value);
                }
            }
            else if (section.Data.Span[0] == 2 && ProgrammeMapParser.ParsePmt(section.Pid, section.Data.Span) is { } map
                && (lease.Channel.ServiceId is null || lease.Channel.ServiceId == map.ServiceId))
            {
                ServiceId = map.ServiceId;
                programme = map;
                pmt = section.Data.ToArray();
            }
        }
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
                var count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
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
