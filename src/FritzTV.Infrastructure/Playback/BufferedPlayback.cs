using FritzTV.Core.Models;
using FritzTV.Infrastructure.Dvb;
using FritzTV.Infrastructure.Timeshift;
using FritzTV.Infrastructure.Transport;

namespace FritzTV.Infrastructure.Playback;

public sealed class BufferedPlayback : IAsyncDisposable
{
    private readonly ChannelLease lease;
    private readonly Guid deviceId;
    private readonly string directory;
    private readonly CancellationTokenSource lifetime = new();
    private readonly PsiSectionAssembler assembler = new();
    private readonly Dictionary<ushort, int> pmtPids = [];
    private readonly Dictionary<(DvbServiceIdentity Service, ushort EventId), Programme> guide = [];
    private readonly Lock metadataLock = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task receiveTask;
    private int disposed;

    private BufferedPlayback(ChannelLease lease, AppSettings settings)
    {
        this.lease = lease;
        deviceId = lease.Channel.DeviceId;
        directory = Path.Combine(settings.BufferDirectory, Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        Buffer = new IndexedTimeshiftBuffer(directory, settings.TimeshiftDuration, settings.TimeshiftBytes, settings.FreeSpaceReserveBytes);
        assembler.SectionReceived += OnSectionReceived;
        receiveTask = ReceiveAsync(lifetime.Token);
    }

    public IndexedTimeshiftBuffer Buffer { get; }

    public TvChannel Channel => lease.Channel;

    public ProgrammeMap? Programme { get; private set; }

    public Exception? Failure { get; private set; }

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

    public static async Task<BufferedPlayback> StartAsync(SatIpSessionManager manager, SatIpDevice device, TvChannel channel, AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(settings);
        var acquiredLease = await manager.AcquireAsync(device, channel, TunerPriority.Viewing, cancellationToken).ConfigureAwait(false);
        var playback = new BufferedPlayback(acquiredLease, settings);
        try
        {
            await playback.ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            return playback;
        }
        catch
        {
            await playback.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
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
            await foreach (var batch in lease.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var decodable = InspectTransport(batch);
                await Buffer.AppendAsync(batch, cancellationToken).ConfigureAwait(false);
                if (Programme is not null && decodable)
                {
                    ready.TrySetResult();
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("The tuner stream ended before viewing was stopped.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ready.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.Threading.Channels.ChannelClosedException)
        {
            Failure = exception;
            ready.TrySetException(exception);
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
}
