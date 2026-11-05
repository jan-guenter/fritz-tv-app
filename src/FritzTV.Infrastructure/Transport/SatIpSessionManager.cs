using FritzTV.Core.Exceptions;
using FritzTV.Core.Models;

namespace FritzTV.Infrastructure.Transport;

public sealed class SatIpSessionManager(int firstPort = 50000, int lastPort = 50127) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, SessionEntry> sessions = new(StringComparer.Ordinal);
    private bool disposed;
    private IReadOnlyList<TunerUsage> usageSnapshot = Array.Empty<TunerUsage>();
    private readonly Lock openingLock = new();
    private CancellationTokenSource? openingSource;
    private TunerPriority openingPriority;

    public IReadOnlyList<TunerUsage> GetUsageSnapshot() => Volatile.Read(ref usageSnapshot);

    public async Task<ChannelLease> AcquireAsync(SatIpDevice device, TvChannel channel, TunerPriority priority, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(channel);
        await WaitForAcquisitionGateAsync(priority, cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var key = device.Id.ToString("D", System.Globalization.CultureInfo.InvariantCulture) + ":" + SatIpTuning.Key(channel.StreamUri);
            var pids = SatIpTuning.Pids(channel.StreamUri);
            sessions.TryGetValue(key, out var entry);
            if (entry is not null && !entry.Session.IsRunning)
            {
                sessions.Remove(key);
                await entry.Session.DisposeAsync().ConfigureAwait(false);
                entry = null;
            }

            if (entry is null)
            {
                await MakeRoomAsync(device, priority).ConfigureAwait(false);
                var session = await OpenSessionAsync(SatIpTuning.WithPids(channel.StreamUri, pids), priority, cancellationToken).ConfigureAwait(false);
                entry = new SessionEntry(device.Id, device.TunerCount, session);
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
            return new ChannelLease(channel, entry.Session, subscription, pids, (requested, token) => UpdateConsumerPidsAsync(key, id, requested, token), () => ReleaseAsync(key, id));
        }
        finally
        {
            UpdateUsage();
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
            UpdateUsage();
            gate.Release();
        }
    }

    private void UpdateUsage()
    {
        var snapshot = sessions.Values.GroupBy(entry => entry.DeviceId).Select(group => new TunerUsage(group.Key, group.Count(), group.First().TunerCount,
            group.Sum(entry => entry.Consumers.Count), CountConsumers(group, TunerPriority.Recording), CountConsumers(group, TunerPriority.Viewing), CountConsumers(group, TunerPriority.GuideScan))).ToArray();
        Volatile.Write(ref usageSnapshot, Array.AsReadOnly(snapshot));
    }

    private static int CountConsumers(IEnumerable<SessionEntry> entries, TunerPriority priority) => entries.Sum(entry => entry.Consumers.Values.Count(consumer => consumer.Priority == priority));

    private async Task WaitForAcquisitionGateAsync(TunerPriority priority, CancellationToken cancellationToken)
    {
        do
        {
            CancelLowerPriorityOpening(priority);
        }
        while (!await gate.WaitAsync(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false));
    }

    private void CancelLowerPriorityOpening(TunerPriority requested)
    {
        CancellationTokenSource? cancel = null;
        lock (openingLock)
        {
            if (openingSource is not null && requested > openingPriority)
            {
                cancel = openingSource;
            }
        }

        try
        {
            cancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The previous opening completed between releasing the lock and cancellation.
        }
    }

    private async Task<SatIpSession> OpenSessionAsync(Uri source, TunerPriority priority, CancellationToken cancellationToken)
    {
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (openingLock)
        {
            openingSource = opening;
            openingPriority = priority;
        }

        try
        {
            return await SatIpSession.OpenAsync(source, firstPort, lastPort, opening.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TunerUnavailableException("Tuning was interrupted by a higher-priority consumer.", exception);
        }
        finally
        {
            lock (openingLock)
            {
                openingSource = null;
            }
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
            throw new TunerUnavailableException();
        }

        sessions.Remove(victim.Key);
        victim.Value.Session.Abort(new TunerUnavailableException("Live viewing or guide scanning was interrupted to start a higher-priority recording."));
        await victim.Value.Session.DisposeAsync().ConfigureAwait(false);
    }

    private async Task UpdateConsumerPidsAsync(string key, Guid id, IReadOnlySet<int> requested, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!sessions.TryGetValue(key, out var entry) || !entry.Consumers.TryGetValue(id, out var consumer) || !entry.Session.IsRunning)
            {
                throw new IOException("The tuner lease ended before its programme PID selection could be updated.");
            }

            var previous = entry.Pids;
            var next = entry.Consumers.Where(item => item.Key != id).SelectMany(item => item.Value.Pids).Concat(requested).ToHashSet();
            await entry.Session.UpdatePidsAsync(next.Except(previous), previous.Except(next), cancellationToken).ConfigureAwait(false);
            entry.Consumers[id] = (requested, consumer.Priority);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ReleaseAsync(string key, Guid id)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            sessions.TryGetValue(key, out var entry);
            if (entry is not null && !entry.Session.IsRunning)
            {
                sessions.Remove(key);
                await entry.Session.DisposeAsync().ConfigureAwait(false);
                entry = null;
            }

            if (entry is null)
            {
                return;
            }

            var previous = entry.Pids;
            if (!entry.Consumers.Remove(id))
            {
                return;
            }
            if (entry.Consumers.Count == 0)
            {
                sessions.Remove(key);
                await entry.Session.DisposeAsync().ConfigureAwait(false);
            }
            else if (entry.Session.IsRunning)
            {
                await entry.Session.UpdatePidsAsync([], previous.Except(entry.Pids), CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            UpdateUsage();
            gate.Release();
        }
    }

    private sealed class SessionEntry(Guid deviceId, int tunerCount, SatIpSession session)
    {
        public Guid DeviceId { get; } = deviceId;

        public int TunerCount { get; } = tunerCount;

        public SatIpSession Session { get; } = session;

        public Dictionary<Guid, (IReadOnlySet<int> Pids, TunerPriority Priority)> Consumers { get; } = [];

        public IReadOnlySet<int> Pids => Consumers.Values.SelectMany(consumer => consumer.Pids).ToHashSet();
    }
}
