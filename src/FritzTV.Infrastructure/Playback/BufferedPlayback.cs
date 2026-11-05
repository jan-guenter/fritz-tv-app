using FritzTV.Core.Models;
using FritzTV.Core.Exceptions;
using FritzTV.Infrastructure.Dvb;
using FritzTV.Infrastructure.Timeshift;
using FritzTV.Infrastructure.Transport;
using FritzTV.Infrastructure.Persistence;

namespace FritzTV.Infrastructure.Playback;

public sealed class BufferedPlayback : IAsyncDisposable
{
    private ChannelLease lease;
    private readonly SatIpSessionManager manager;
    private readonly SatIpDevice device;
    private readonly Guid deviceId;
    private readonly DvbChannelResolver resolver;
    private TvChannel resolvedChannel;
    private readonly string directory;
    private readonly CancellationTokenSource lifetime = new();
    private readonly PsiSectionAssembler assembler = new();
    private readonly Dictionary<ushort, int> pmtPids = [];
    private readonly Dictionary<(DvbServiceIdentity Service, ushort EventId), Programme> guide = [];
    private readonly Lock metadataLock = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task receiveTask;
    private int disposed;
    private int references = 1;

    private BufferedPlayback(ChannelLease lease, AppSettings settings, SatIpSessionManager manager, SatIpDevice device)
    {
        this.lease = lease;
        this.manager = manager;
        this.device = device;
        deviceId = lease.Channel.DeviceId;
        resolvedChannel = lease.Channel;
        resolver = new DvbChannelResolver(deviceId);
        directory = Path.Combine(settings.BufferDirectory, Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        Buffer = new IndexedTimeshiftBuffer(directory, settings.TimeshiftDuration, settings.TimeshiftBytes, settings.FreeSpaceReserveBytes);
        assembler.SectionReceived += OnSectionReceived;
        receiveTask = ReceiveAsync(lifetime.Token);
    }

    public IndexedTimeshiftBuffer Buffer { get; }

    public TvChannel Channel => resolvedChannel;

    public ProgrammeMap? Programme { get; private set; }

    public Exception? Failure { get; private set; }

    public long DiscontinuityCount { get; private set; }

    public bool IsRunning => !receiveTask.IsCompleted;

    public IReadOnlyList<Programme> GuideSnapshot
    {
        get
        {
            lock (metadataLock)
            {
                return guide.Values.OrderBy(item => item.Start).ToArray();
            }
        }
    }

    public static Task<BufferedPlayback> StartAsync(SatIpSessionManager manager, SatIpDevice device, TvChannel channel, AppSettings settings, CancellationToken cancellationToken = default) => StartAsync(manager, device, channel, settings, null, cancellationToken);

    public static async Task<BufferedPlayback> StartAsync(SatIpSessionManager manager, SatIpDevice device, TvChannel channel, AppSettings settings, AppRepository? repository, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(settings);
        var acquiredLease = await manager.AcquireAsync(device, channel, TunerPriority.Viewing, cancellationToken).ConfigureAwait(false);
        BufferedPlayback? playback = null;
        try
        {
            playback = new BufferedPlayback(acquiredLease, settings, manager, device);
            await playback.ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            if (repository is not null && playback.resolver.Resolve(channel) is { } identity)
            {
                playback.resolvedChannel = await repository.ResolveChannelAsync(channel.Id, identity, cancellationToken).ConfigureAwait(false);
            }

            return playback;
        }
        catch
        {
            if (playback is not null)
            {
                await playback.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await acquiredLease.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    public IAsyncDisposable Retain()
    {
        lock (metadataLock)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            references++;
        }

        return new PlaybackRetention(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            Buffer.ReleaseViewingRetention();
            await ReleaseReferenceAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask ReleaseReferenceAsync()
    {
        lock (metadataLock)
        {
            if (--references != 0)
            {
                return;
            }
        }

        await lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await receiveTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        assembler.SectionReceived -= OnSectionReceived;
        await Buffer.DisposeAsync().ConfigureAwait(false);
        await lease.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await ReadLeaseAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (TimeshiftStorageException exception)
                {
                    Failure = exception;
                    ready.TrySetException(exception);
                    return;
                }
                catch (TunerUnavailableException exception)
                {
                    Failure = exception;
                    ready.TrySetException(exception);
                    return;
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                {
                    Failure = exception;
                    DiscontinuityCount++;
                    if (!ready.Task.IsCompletedSuccessfully)
                    {
                        ready.TrySetException(exception);
                        return;
                    }

                    await ReconnectAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ready.TrySetCanceled(cancellationToken);
        }
        catch (TunerUnavailableException exception)
        {
            Failure = exception;
        }
        finally
        {
            await Buffer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ReadLeaseAsync(CancellationToken cancellationToken)
    {
        await foreach (var batch in lease.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var decodable = InspectTransport(batch);
            try
            {
                await Buffer.AppendAsync(batch, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new TimeshiftStorageException(exception.Message, exception);
            }

            if (Programme is not null && decodable && Buffer.CanStartPlayback)
            {
                ready.TrySetResult();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new IOException("The tuner stream ended before viewing was stopped.");
    }

    private async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        await lease.DisposeAsync().ConfigureAwait(false);
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(attempt++, 5))), cancellationToken).ConfigureAwait(false);
            try
            {
                lease = await manager.AcquireAsync(device, Channel, TunerPriority.Viewing, cancellationToken).ConfigureAwait(false);
                Failure = null;
                return;
            }
            catch (TunerUnavailableException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                Failure = exception;
            }
        }
    }

    private bool InspectTransport(ReadOnlyMemory<byte> batch)
    {
        var entryPoint = false;
        for (var offset = 0; offset + 188 <= batch.Length; offset += 188)
        {
            var packet = batch.Span.Slice(offset, 188);
            if (!TsPacket.TryParse(packet, out var ts))
            {
                continue;
            }

            if (ts.Pid is 0 or 17 or 18 || pmtPids.ContainsValue(ts.Pid))
            {
                assembler.Push(packet);
            }

            entryPoint |= ts.StartsPayload && (Channel.Kind == ChannelKind.Radio || ts.RandomAccess);
        }

        return entryPoint;
    }

    private void OnSectionReceived(object? sender, PsiSectionEventArgs section)
    {
        var data = section.Data.Span;
        resolver.Observe(section);
        if (data[0] == 0)
        {
            foreach (var (id, pid) in ProgrammeMapParser.ParsePat(data))
            {
                pmtPids[id] = pid;
            }
        }
        else if (data[0] == 2 && ProgrammeMapParser.ParsePmt(section.Pid, data) is { } map
            && (Channel.ServiceId is null || Channel.ServiceId == map.ServiceId))
        {
            Programme = map;
            Buffer.SetProgramme(map);
        }
        else if (data[0] is >= 0x4e and <= 0x6f)
        {
            lock (metadataLock)
            {
                foreach (var programme in EitParser.Parse(deviceId, data))
                {
                    guide[(programme.Service, programme.EventId)] = programme;
                }
            }
        }
    }
    private sealed class PlaybackRetention(BufferedPlayback owner) : IAsyncDisposable
    {
        private int released;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                await owner.ReleaseReferenceAsync().ConfigureAwait(false);
            }
        }
    }

}
