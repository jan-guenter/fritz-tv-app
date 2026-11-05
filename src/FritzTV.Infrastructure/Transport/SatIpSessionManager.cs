using FritzTV.Core.Models;

namespace FritzTV.Infrastructure.Transport;

public sealed class SatIpSessionManager(int firstPort = 50000, int lastPort = 50127) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, SessionEntry> sessions = new(StringComparer.Ordinal);
    private bool disposed;

    public async Task<ChannelLease> AcquireAsync(SatIpDevice device, TvChannel channel, TunerPriority priority, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(channel);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var key = device.Id.ToString("D", System.Globalization.CultureInfo.InvariantCulture) + ":" + SatIpTuning.Key(channel.StreamUri);
            var pids = SatIpTuning.Pids(channel.StreamUri);
            if (!sessions.TryGetValue(key, out var entry))
            {
                await MakeRoomAsync(device, priority).ConfigureAwait(false);
                var session = await SatIpSession.OpenAsync(SatIpTuning.WithPids(channel.StreamUri, pids), firstPort, lastPort, cancellationToken).ConfigureAwait(false);
                entry = new SessionEntry(device.Id, session);
                sessions.Add(key, entry);
            }
            else
            {
                var added = pids.Except(entry.Pids).ToArray();
                await entry.Session.UpdatePidsAsync(added, [], cancellationToken).ConfigureAwait(false);
            }

            var id = Guid.NewGuid();
            entry.Consumers.Add(id, (pids, priority));
            var subscription = entry.Session.Subscribe(discardOldest: priority == TunerPriority.GuideScan);
            return new ChannelLease(channel, entry.Session, subscription, pids, () => ReleaseAsync(key, id));
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (var entry in sessions.Values)
            {
                await entry.Session.DisposeAsync().ConfigureAwait(false);
            }

            sessions.Clear();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task MakeRoomAsync(SatIpDevice device, TunerPriority requested)
    {
        var allocated = sessions.Where(item => item.Value.DeviceId == device.Id).ToArray();
        if (allocated.Length < device.TunerCount)
        {
            return;
        }

        var victim = allocated.Where(item => item.Value.Consumers.Values.All(consumer => consumer.Priority < requested))
            .OrderBy(item => item.Value.Consumers.Values.Max(consumer => consumer.Priority)).FirstOrDefault();
        if (victim.Value is null)
        {
            throw new IOException("All SAT>IP tuners are reserved by equal or higher priority consumers.");
        }

        sessions.Remove(victim.Key);
        await victim.Value.Session.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ReleaseAsync(string key, Guid id)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!sessions.TryGetValue(key, out var entry))
            {
                return;
            }

            var previous = entry.Pids;
            entry.Consumers.Remove(id);
            if (entry.Consumers.Count == 0)
            {
                sessions.Remove(key);
                await entry.Session.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await entry.Session.UpdatePidsAsync([], previous.Except(entry.Pids), CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed class SessionEntry(Guid deviceId, SatIpSession session)
    {
        public Guid DeviceId { get; } = deviceId;

        public SatIpSession Session { get; } = session;

        public Dictionary<Guid, (IReadOnlySet<int> Pids, TunerPriority Priority)> Consumers { get; } = [];

        public IReadOnlySet<int> Pids => Consumers.Values.SelectMany(consumer => consumer.Pids).ToHashSet();
    }
}
