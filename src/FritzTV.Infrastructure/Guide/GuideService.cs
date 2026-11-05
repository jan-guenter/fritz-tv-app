using System.Collections.Concurrent;
using FritzTV.Core.Guide;
using FritzTV.Core.Models;
using FritzTV.Infrastructure.Dvb;
using FritzTV.Infrastructure.Persistence;
using FritzTV.Infrastructure.Transport;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FritzTV.Infrastructure.Guide;

public sealed partial class GuideService(AppRepository repository, SatIpSessionManager sessions, ILogger<GuideService> logger, TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(DvbServiceIdentity Service, ushort EventId), Programme> known = new();
    private readonly ConcurrentDictionary<(DvbServiceIdentity Service, ushort EventId), Programme> pending = new();
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private readonly Lock statusLock = new();
    private readonly Lock ingestionLock = new();
    private readonly SemaphoreSlim cacheGate = new(1, 1);
    private bool cacheLoaded;
    private GuideStatus status = new(false, 0, 0, null, 0, null, null);
    private int refreshRequested = 1;
    private DateTimeOffset? retryAt;

    public GuideStatus Status => Volatile.Read(ref status);

    public void RequestRefresh()
    {
        if (!Status.IsScanning)
        {
            Interlocked.Exchange(ref refreshRequested, 1);
        }
    }

    public void Ingest(IEnumerable<Programme> programmes) => IngestCore(programmes, persist: true);

    private void IngestCore(IEnumerable<Programme> programmes, bool persist)
    {
        ArgumentNullException.ThrowIfNull(programmes);
        var now = clock.GetUtcNow();
        DateTimeOffset? latest = null;
        var changed = false;
        lock (ingestionLock)
        {
            foreach (var programme in programmes)
            {
                var key = (programme.Service, programme.EventId);
                if (programme.End < now.AddDays(-1) || programme.Start > now.AddDays(14) || (known.Count >= 100000 && !known.ContainsKey(key)))
                {
                    continue;
                }
                if (known.TryGetValue(key, out var previous) && !ProgrammeUpdatePolicy.ShouldReplace(previous, programme, now))
                {
                    continue;
                }

                known[key] = programme;
                if (persist)
                {
                    pending[key] = programme;
                }
                else
                {
                    pending.TryRemove(key, out _);
                }
                changed = true;
                latest = latest is null || programme.End > latest ? programme.End : latest;
            }

        }

        if (changed)
        {
            UpdateStatus(current => current with { ProgrammeCount = known.Count, CoverageUntil = current.CoverageUntil is null || latest > current.CoverageUntil ? latest : current.CoverageUntil });
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!await scanGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await LoadCachedAsync(cancellationToken).ConfigureAwait(false);
            await ScanAsync(cancellationToken).ConfigureAwait(false);
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            UpdateStatus(current => current with { IsScanning = false });
            scanGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task scan = Task.CompletedTask;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await LoadCachedAsync(stoppingToken).ConfigureAwait(false);
                    await FlushAsync(stoppingToken).ConfigureAwait(false);
                    var settings = await repository.GetSettingsAsync(stoppingToken).ConfigureAwait(false);
                    if (scan.IsCompleted && (Interlocked.Exchange(ref refreshRequested, 0) != 0 || Status.LastRefreshUtc is null || (retryAt is { } retry ? clock.GetUtcNow() >= retry : clock.GetUtcNow() - Status.LastRefreshUtc >= settings.GuideRefreshInterval)))
                    {
                        scan = RunScanSafelyAsync(stoppingToken);
                    }
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException or System.Data.Common.DbException)
                {
                    UpdateStatus(current => current with { LastError = exception.Message });
                    ScanFailed(logger, exception);
                }

                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await scan.ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        scanGate.Dispose();
        cacheGate.Dispose();
    }

    private async Task LoadCachedAsync(CancellationToken cancellationToken)
    {
        await cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!cacheLoaded)
            {
                var now = clock.GetUtcNow();
                var cached = await repository.GetProgrammesAsync(now.AddDays(-1), now.AddDays(14), cancellationToken).ConfigureAwait(false);
                IngestCore(cached, persist: false);
                cacheLoaded = true;
            }
        }
        finally
        {
            cacheGate.Release();
        }
    }

    private async Task RunScanSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or HttpRequestException or System.Data.Common.DbException or System.ComponentModel.Win32Exception)
        {
            UpdateStatus(current => current with { LastError = exception.Message });
            ScanFailed(logger, exception);
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        var devices = (await repository.GetDevicesAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(device => device.Id);
        var channels = await repository.GetChannelsAsync(cancellationToken).ConfigureAwait(false);
        var groups = channels.GroupBy(channel => channel.DeviceId.ToString("D", System.Globalization.CultureInfo.InvariantCulture) + ":" + SatIpTuning.Key(channel.StreamUri), StringComparer.Ordinal).ToArray();
        UpdateStatus(current => current with { IsScanning = true, CompletedFrequencies = 0, TotalFrequencies = groups.Length, LastError = null });
        var blocked = false;
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (devices.TryGetValue(group.First().DeviceId, out var device))
            {
                blocked |= !await ScanFrequencyAsync(device, group.ToArray(), cancellationToken).ConfigureAwait(false);
            }

            UpdateStatus(current => current with { CompletedFrequencies = current.CompletedFrequencies + 1 });
        }

        retryAt = blocked ? clock.GetUtcNow().AddMinutes(5) : null;
        UpdateStatus(current => current with { LastRefreshUtc = clock.GetUtcNow() });
        await repository.PruneProgrammesAsync(clock.GetUtcNow().AddDays(-1), clock.GetUtcNow().AddDays(14), cancellationToken).ConfigureAwait(false);
        foreach (var expired in known.Where(item => item.Value.End < clock.GetUtcNow().AddDays(-1)).Select(item => item.Key))
        {
            known.TryRemove(expired, out _);
        }
    }

    private async Task<bool> ScanFrequencyAsync(SatIpDevice device, TvChannel[] channels, CancellationToken cancellationToken)
    {
        var source = channels[0] with { StreamUri = SatIpTuning.WithPids(channels[0].StreamUri, [0, 16, 17, 18, 20]) };
        try
        {
            var lease = await sessions.AcquireAsync(device, source, TunerPriority.GuideScan, cancellationToken).ConfigureAwait(false);
            await using var leaseScope = lease.ConfigureAwait(false);
            var resolver = new DvbChannelResolver(device.Id);
            var coverage = new EitCoverageTracker();
            var assembler = new PsiSectionAssembler();
            assembler.SectionReceived += (_, section) =>
            {
                resolver.Observe(section);
                if (section.Data.Span[0] == 0x42)
                {
                    coverage.ExpectServices(resolver.ExpectedScheduleServices);
                }
                coverage.Observe(section.Data.Span);
                Ingest(EitParser.Parse(device.Id, section.Data.Span));
            };
            await ReadScanAsync(lease, assembler, coverage, cancellationToken).ConfigureAwait(false);
            if (resolver.HasTransportIdentity)
            {
                foreach (var channel in channels)
                {
                    if (resolver.Resolve(channel) is { } identity)
                    {
                        await repository.ResolveChannelAsync(channel.Id, identity, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            UpdateStatus(current => current with { LastError = exception.Message });
            FrequencySkipped(logger, device.Name, exception.Message);
            return false;
        }
    }

    private async Task ReadScanAsync(ChannelLease lease, PsiSectionAssembler assembler, EitCoverageTracker coverage, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var started = clock.GetTimestamp();
        try
        {
            await foreach (var batch in lease.ReadAllAsync(deadline.Token).ConfigureAwait(false))
            {
                for (var offset = 0; offset + 188 <= batch.Length; offset += 188)
                {
                    var packet = batch.Span.Slice(offset, 188);
                    if (TsPacket.TryParse(packet, out var ts) && ts.Pid is 0 or 17 or 18)
                    {
                        assembler.Push(packet);
                    }
                }

                if (coverage.IsComplete && clock.GetElapsedTime(started) >= TimeSpan.FromSeconds(10))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A bounded scan keeps all collected data and still resolves channel identities.
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        var batch = new List<Programme>();
        foreach (var entry in pending.Take(2000))
        {
            if (pending.TryRemove(entry.Key, out var programme))
            {
                batch.Add(programme);
            }
        }

        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            await repository.SaveProgrammesAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            foreach (var programme in batch)
            {
                pending.TryAdd((programme.Service, programme.EventId), programme);
            }

            throw;
        }
    }

    private void UpdateStatus(Func<GuideStatus, GuideStatus> update)
    {
        lock (statusLock)
        {
            Volatile.Write(ref status, update(status));
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Guide refresh failed.")]
    private static partial void ScanFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Guide frequency on {Device} was skipped: {Reason}")]
    private static partial void FrequencySkipped(ILogger logger, string device, string reason);
}
