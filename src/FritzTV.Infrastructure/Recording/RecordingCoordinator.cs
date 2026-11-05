using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using FritzTV.Core.Exceptions;
using FritzTV.Core.Models;
using FritzTV.Core.Scheduling;
using FritzTV.Infrastructure.Persistence;
using FritzTV.Infrastructure.Playback;
using FritzTV.Infrastructure.Power;
using FritzTV.Infrastructure.Scheduling;
using FritzTV.Infrastructure.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FritzTV.Infrastructure.Recording;

public sealed class RecordingCoordinator : IAsyncDisposable
{
    private static readonly Action<ILogger, Guid, Exception?> LogCaptureFailure = LoggerMessage.Define<Guid>(LogLevel.Error, new EventId(2, "CaptureFailed"), "Recording {RecordingId} failed outside its capture handler; source data was retained.");
    private readonly AppRepository repository;
    private readonly TimeProvider clock;
    private readonly IRecordingInputFactory inputs;
    private readonly IRecordingFinalizer finalizer;
    private readonly IPowerInhibitor power;
    private readonly ILogger<RecordingCoordinator> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, CaptureJob> active = new();
    private bool recovered;
    private DateTimeOffset nextMaterialization = DateTimeOffset.MinValue;
    private DateTimeOffset nextGuideUpdate = DateTimeOffset.MinValue;
    private int disposed;

    public RecordingCoordinator(AppRepository repository, SatIpSessionManager sessions, TimeProvider? timeProvider = null, IRecordingInputFactory? inputFactory = null, IRecordingFinalizer? finalizer = null, IPowerInhibitor? powerInhibitor = null, ILogger<RecordingCoordinator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(sessions);
        this.repository = repository;
        clock = timeProvider ?? TimeProvider.System;
        inputs = inputFactory ?? new SatIpRecordingInputFactory(repository, sessions);
        this.finalizer = finalizer ?? new RecordingFinalizer();
        power = powerInhibitor ?? new SystemPowerInhibitor();
        this.logger = logger ?? NullLogger<RecordingCoordinator>.Instance;
    }

    public event EventHandler? Changed;

    public async Task<IReadOnlyList<RecordingInstance>> GetRecordingsAsync(CancellationToken cancellationToken = default)
    {
        var recordings = await repository.GetRecordingsAsync(cancellationToken).ConfigureAwait(false);
        return recordings.OrderByDescending(item => item.Start).ThenBy(item => item.Created).ToArray();
    }

    public async Task<IReadOnlyList<RecordingInstance>> CreateTimerAsync(RecordingDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.Title))
        {
            throw new ArgumentException("A timer title is required.", nameof(definition));
        }
        _ = TimeZoneInfo.FindSystemTimeZoneById(definition.TimeZoneId);
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = await repository.GetChannelAsync(definition.ChannelId, cancellationToken).ConfigureAwait(false);
            if (channel is null || !channel.IsAvailable)
            {
                throw new ArgumentException("The timer channel is no longer configured.", nameof(definition));
            }

            definition = definition with { ChannelId = channel.Id };
            var expanded = ScheduleCalculator.Expand(definition, clock.GetUtcNow().AddDays(-1), clock.GetUtcNow().AddDays(14), clock.GetUtcNow());
            await repository.SaveDefinitionAsync(definition, cancellationToken).ConfigureAwait(false);
            await MaterializeDefinitionAsync(definition, expanded, cancellationToken).ConfigureAwait(false);
            await UpdatePredictedConflictsAsync(cancellationToken).ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);
            return (await repository.GetRecordingsAsync(cancellationToken).ConfigureAwait(false)).Where(item => item.DefinitionId == definition.Id).OrderBy(item => item.Start).ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RecordingInstance> StartInstantAsync(TvChannel channel, AppSettings settings, BufferedPlayback? playback = null, DateTimeOffset? from = null, DateTimeOffset? until = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        channel = await repository.GetChannelAsync(channel.Id, cancellationToken).ConfigureAwait(false) ?? channel;
        if (!channel.IsAvailable)
        {
            throw new InvalidOperationException("The channel is no longer available in the device playlist.");
        }

        var now = clock.GetUtcNow();
        if (until is { } end && end <= now)
        {
            throw new ArgumentException("An instant recording must end in the future.", nameof(until));
        }

        var instance = new RecordingInstance(Guid.NewGuid(), Guid.Empty, channel.Id, channel.Name, from ?? now, until ?? DateTimeOffset.MaxValue, now, RecordingState.Scheduled);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await repository.SaveRecordingAsync(instance, cancellationToken).ConfigureAwait(false);
            return await StartCaptureAsync(instance, channel, settings, playback, from, settings.RetainTransportStreams, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await SaveAsync(instance with { State = RecordingState.Failed, Error = "The instant recording request was canceled before capture started." }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await SaveAsync(instance with { State = RecordingState.Failed, Error = exception.Message }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (active.TryGetValue(id, out var job))
        {
            job.RequestStop(interrupted: false);
            await job.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ExtendAsync(Guid id, DateTimeOffset end, CancellationToken cancellationToken = default)
    {
        if (end <= clock.GetUtcNow())
        {
            throw new ArgumentException("The new end must be in the future.", nameof(end));
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var instance = (await repository.GetRecordingsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(item => item.Id == id)
                ?? throw new ArgumentException("The recording does not exist.", nameof(id));
            if (instance.State is not (RecordingState.Scheduled or RecordingState.Conflict or RecordingState.Recording))
            {
                throw new InvalidOperationException("Only scheduled or active recordings can be extended.");
            }

            instance = instance with { End = end };
            if (active.TryGetValue(id, out var job))
            {
                job.Instance = instance;
            }

            await SaveAsync(instance, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CancelTimerAsync(Guid definitionId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await repository.DeleteDefinitionAsync(definitionId, cancellationToken).ConfigureAwait(false);
            foreach (var instance in await repository.GetRecordingsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (instance.DefinitionId == definitionId && instance.State is RecordingState.Scheduled or RecordingState.Conflict)
                {
                    await repository.DeleteRecordingAsync(instance.Id, cancellationToken).ConfigureAwait(false);
                }
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RetryFinalizeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (active.ContainsKey(id))
        {
            throw new InvalidOperationException("The recording is still active.");
        }

        var instance = (await repository.GetRecordingsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(item => item.Id == id)
            ?? throw new ArgumentException("The recording does not exist.", nameof(id));
        if (instance.State is not (RecordingState.Partial or RecordingState.Failed or RecordingState.Finalizing))
        {
            throw new InvalidOperationException("Only incomplete recordings can be finalized again.");
        }

        var channel = await repository.GetChannelAsync(instance.ChannelId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The recording channel is no longer configured.");
        var definitions = await repository.GetDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        await FinalizeCaptureAsync(instance, channel, definitions.FirstOrDefault(item => item.Id == instance.DefinitionId)?.RetainTransportStream ?? true,
            partial: instance.State == RecordingState.Partial, instance.Error, cancellationToken).ConfigureAwait(false);
    }

    public async Task RunDueAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!recovered)
            {
                await RecoverAsync(cancellationToken).ConfigureAwait(false);
                recovered = true;
            }

            if (clock.GetUtcNow() >= nextGuideUpdate)
            {
                await UpdateGuideTimersAsync(cancellationToken).ConfigureAwait(false);
                nextGuideUpdate = clock.GetUtcNow().AddSeconds(30);
            }

            var definitions = await repository.GetDefinitionsAsync(cancellationToken).ConfigureAwait(false);
            if (clock.GetUtcNow() >= nextMaterialization)
            {
                foreach (var definition in definitions)
                {
                    var expanded = ScheduleCalculator.Expand(definition, clock.GetUtcNow().AddDays(-31), clock.GetUtcNow().AddDays(14), clock.GetUtcNow());
                    await MaterializeDefinitionAsync(definition, expanded, cancellationToken).ConfigureAwait(false);
                }

                nextMaterialization = clock.GetUtcNow().AddDays(1);
            }

            foreach (var job in active.Values.Where(job => job.Instance.End <= clock.GetUtcNow()))
            {
                job.RequestStop(interrupted: false);
            }

            await UpdatePredictedConflictsAsync(cancellationToken).ConfigureAwait(false);
            await StartDueInstancesAsync(definitions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        foreach (var job in active.Values)
        {
            job.RequestStop(interrupted: true);
        }

        await Task.WhenAll(active.Values.Select(job => job.Completion)).ConfigureAwait(false);
        gate.Dispose();
    }

    private async Task StartDueInstancesAsync(IReadOnlyList<RecordingDefinition> definitions, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var settings = await repository.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var recordings = await repository.GetRecordingsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var instance in recordings.Where(item => item.State is RecordingState.Scheduled or RecordingState.Conflict).OrderBy(item => item.Start).ThenBy(item => item.Created).ThenBy(item => item.Id))
        {
            if (instance.End <= now)
            {
                await SaveAsync(instance with { State = RecordingState.Missed, Error = "The computer or application was unavailable during the recording interval." }, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (instance.Start > now || active.ContainsKey(instance.Id))
            {
                continue;
            }

            var channel = await repository.GetChannelAsync(instance.ChannelId, cancellationToken).ConfigureAwait(false);
            if (channel is null || !channel.IsAvailable)
            {
                await SaveAsync(instance with { State = RecordingState.Failed, Error = "The timer channel is no longer configured." }, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (active.Values.Any(job => job.Channel.DeviceId == channel.DeviceId && job.Reconnecting))
            {
                continue;
            }

            try
            {
                await StartCaptureAsync(instance, channel, settings, null, null, definitions.FirstOrDefault(item => item.Id == instance.DefinitionId)?.RetainTransportStream ?? false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                await SaveAsync(instance with { State = exception is TunerUnavailableException ? RecordingState.Conflict : RecordingState.Failed, Error = exception.Message }, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task UpdatePredictedConflictsAsync(CancellationToken cancellationToken)
    {
        const string prediction = "Predicted tuner conflict: overlapping recordings need more independent tuning frequencies than this device provides.";
        var recordings = await repository.GetRecordingsAsync(cancellationToken).ConfigureAwait(false);
        var channels = await repository.GetChannelsAsync(cancellationToken).ConfigureAwait(false);
        var devices = await repository.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        var conflicts = RecordingConflictPlanner.FindConflicts(recordings, channels, devices, clock.GetUtcNow());
        foreach (var instance in recordings.Where(item => item.State is RecordingState.Scheduled or RecordingState.Conflict))
        {
            if (conflicts.Contains(instance.Id) && instance.State != RecordingState.Conflict)
            {
                await SaveAsync(instance with { State = RecordingState.Conflict, Error = prediction }, cancellationToken).ConfigureAwait(false);
            }
            else if (!conflicts.Contains(instance.Id) && instance.State == RecordingState.Conflict && string.Equals(instance.Error, prediction, StringComparison.Ordinal))
            {
                await SaveAsync(instance with { State = RecordingState.Scheduled, Error = null }, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task MaterializeDefinitionAsync(RecordingDefinition definition, IReadOnlyList<RecordingInstance> expanded, CancellationToken cancellationToken)
    {
        var existing = await repository.GetRecordingsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var instance in expanded)
        {
            if (!existing.Any(item => item.DefinitionId == definition.Id && item.Start == instance.Start))
            {
                await repository.SaveRecordingAsync(instance, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<RecordingInstance> StartCaptureAsync(RecordingInstance instance, TvChannel channel, AppSettings settings, BufferedPlayback? playback, DateTimeOffset? from, bool retainSource, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(settings.RecordingDirectory);
        CheckFreeSpace(settings);
        IRecordingInput? input = null;
        IAsyncDisposable? inhibition = null;
        FileStream? writer = null;
        CaptureJob? job = null;
        var added = false;
        try
        {
            input = await inputs.OpenAsync(channel, playback, from, cancellationToken).ConfigureAwait(false);
            inhibition = await power.AcquireAsync(cancellationToken).ConfigureAwait(false);
            var source = instance.SourcePath ?? CreateSourcePath(settings.RecordingDirectory, instance);
            writer = new FileStream(source, FileMode.Append, FileAccess.Write, FileShare.Read, 188 * 256, FileOptions.Asynchronous);
            var output = instance.OutputPath ?? Path.ChangeExtension(source, channel.Kind == ChannelKind.Radio ? ".mka" : ".mkv");
            instance = instance with { State = RecordingState.Recording, SourcePath = source, OutputPath = output };
            var late = playback is null && clock.GetUtcNow() - instance.Start > TimeSpan.FromSeconds(5);
            job = new CaptureJob(instance, channel, settings, retainSource, late || instance.Error is not null);
            if (!active.TryAdd(instance.Id, job))
            {
                throw new InvalidOperationException("The recording is already active.");
            }

            added = true;
            await SaveAsync(instance, cancellationToken).ConfigureAwait(false);
            _ = ObserveCaptureAsync(job, input, inhibition, writer);
            return instance;
        }
        catch
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }

            if (inhibition is not null)
            {
                await inhibition.DisposeAsync().ConfigureAwait(false);
            }

            if (input is not null)
            {
                await input.DisposeAsync().ConfigureAwait(false);
            }

            if (added)
            {
                active.TryRemove(instance.Id, out _);
                job!.Done.TrySetResult();
                job.Cancellation.Dispose();
            }

            throw;
        }
    }

    private async Task ObserveCaptureAsync(CaptureJob job, IRecordingInput input, IAsyncDisposable inhibition, FileStream writer)
    {
        try
        {
            await CaptureAsync(job, input, inhibition, writer).ConfigureAwait(false);
            job.Done.TrySetResult();
        }
        catch (Exception exception)
        {
            LogCaptureFailure(logger, job.Instance.Id, exception);
            job.Done.TrySetException(exception);
        }
    }

    private async Task CaptureAsync(CaptureJob job, IRecordingInput input, IAsyncDisposable inhibition, FileStream writer)
    {
        string? error = job.Partial ? job.Instance.Error ?? "Recording started after part of the requested interval was missed." : null;
        job.Error = error;
        var serviceId = job.Channel.ServiceId;
        try
        {
            await using (inhibition.ConfigureAwait(false))
            {
                await using (writer.ConfigureAwait(false))
                {
                    (serviceId, error) = await WriteTransportAsync(job, input, writer, error).ConfigureAwait(false);
                    error = job.Error ?? error;
                }
            }
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
        {
            serviceId = job.ServiceId ?? input.ServiceId ?? serviceId;
            error = job.Error;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            job.Partial = true;
            error = AppendError(error, exception.Message);
        }
        finally
        {
            try
            {
                if (job.Interrupted)
                {
                    await SaveAsync(job.Instance with { State = RecordingState.Partial, Error = AppendError(error, "Capture stopped when the application exited; transport data was retained.") }, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await FinalizeCaptureAsync(job.Instance, job.Channel with { ServiceId = serviceId }, job.RetainSource, job.Partial, error, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                active.TryRemove(job.Instance.Id, out _);
                job.Cancellation.Dispose();
            }
        }
    }

    private async Task<(int? ServiceId, string? Error)> WriteTransportAsync(CaptureJob job, IRecordingInput initialInput, Stream writer, string? error)
    {
        IRecordingInput? input = initialInput;
        int? serviceId = input.ServiceId;
        var retries = 0;
        try
        {
            while (!job.Cancellation.IsCancellationRequested)
            {
                try
                {
                    if (input is null)
                    {
                        input = await ReopenInputAsync(job).ConfigureAwait(false);
                    }
                    job.Reconnecting = false;
                    await WriteInputAsync(job, input, writer).ConfigureAwait(false);
                    serviceId = input.ServiceId ?? serviceId;
                    if (input.LostPackets > 0)
                    {
                        job.Partial = true;
                        error = AppendError(error, input.LostPackets.ToString(CultureInfo.InvariantCulture) + " RTP packets were lost.");
                    }

                    return (serviceId, error);
                }
                catch (ReceptionException exception) when (retries < 3 && !job.Cancellation.IsCancellationRequested)
                {
                    job.Partial = true;
                    job.Reconnecting = true;
                    error = AppendError(error, exception.Message);
                    job.Error = error;
                    retries++;
                    if (input is not null)
                    {
                        serviceId = input.ServiceId ?? serviceId;
                        await input.DisposeAsync().ConfigureAwait(false);
                        input = null;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1 << (retries - 1)), clock, job.Cancellation.Token).ConfigureAwait(false);
                }
            }

            return (serviceId, error);
        }
        finally
        {
            RecordReceptionState(job, input, serviceId);
            if (input is not null)
            {
                await input.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static void RecordReceptionState(CaptureJob job, IRecordingInput? input, int? serviceId)
    {
        if (job.Continuity.Problems > 0)
        {
            job.Partial = true;
            job.Error = AppendError(job.Error, job.Continuity.Problems.ToString(CultureInfo.InvariantCulture) + " transport continuity gaps or invalid packets were observed.");
        }

        if (input is not null)
        {
            job.ServiceId = input.ServiceId ?? serviceId;
            if (input.LostPackets > 0)
            {
                job.Partial = true;
                job.Error = AppendError(job.Error, input.LostPackets.ToString(CultureInfo.InvariantCulture) + " RTP packets were lost.");
            }
        }
    }

    private async Task<IRecordingInput> ReopenInputAsync(CaptureJob job)
    {
        try
        {
            return await inputs.OpenAsync(job.Channel, cancellationToken: job.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
        {
            throw new ReceptionException("Recording reconnection failed: " + exception.Message, exception);
        }
    }

    private async Task WriteInputAsync(CaptureJob job, IRecordingInput input, Stream writer)
    {
        using var readerLifetime = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token);
        var iterator = input.ReadAllAsync(readerLifetime.Token).GetAsyncEnumerator(readerLifetime.Token);
        await using var iteratorDisposal = iterator.ConfigureAwait(false);
        var checkedAt = clock.GetTimestamp();
        while (await ReceiveNextAsync(iterator, readerLifetime).ConfigureAwait(false))
        {
            if (clock.GetUtcNow() >= job.Instance.End)
            {
                return;
            }

            if (clock.GetElapsedTime(checkedAt) >= TimeSpan.FromSeconds(1))
            {
                CheckFreeSpace(job.Settings);
                checkedAt = clock.GetTimestamp();
            }

            job.Continuity.Inspect(iterator.Current.Span);
            await writer.WriteAsync(iterator.Current, job.Cancellation.Token).ConfigureAwait(false);
        }

        if (!job.Cancellation.IsCancellationRequested && clock.GetUtcNow() < job.Instance.End)
        {
            throw new ReceptionException("The tuner stream ended before recording was stopped.");
        }
    }

    private async Task<bool> ReceiveNextAsync(IAsyncEnumerator<ReadOnlyMemory<byte>> iterator, CancellationTokenSource readerLifetime)
    {
        var pending = iterator.MoveNextAsync().AsTask();
        try
        {
            return await pending.WaitAsync(TimeSpan.FromSeconds(15), clock, readerLifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (readerLifetime.IsCancellationRequested)
        {
            await SettleReadAsync(pending).ConfigureAwait(false);
            throw;
        }
        catch (TimeoutException exception)
        {
            await readerLifetime.CancelAsync().ConfigureAwait(false);
            await SettleReadAsync(pending).ConfigureAwait(false);
            throw new ReceptionException("Recording reception timed out.", exception);
        }
        catch (Exception exception) when (exception is IOException or System.Threading.Channels.ChannelClosedException)
        {
            throw new ReceptionException("Recording reception failed: " + exception.Message, exception);
        }
    }

    private static async Task SettleReadAsync(Task<bool> pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or System.Threading.Channels.ChannelClosedException)
        {
            // An async iterator must leave MoveNext before its DisposeAsync may run.
        }
    }

    private async Task FinalizeCaptureAsync(RecordingInstance instance, TvChannel channel, bool retainSource, bool partial, string? error, CancellationToken cancellationToken)
    {
        if (instance.SourcePath is null || instance.OutputPath is null || !File.Exists(instance.SourcePath) || new FileInfo(instance.SourcePath).Length == 0)
        {
            await SaveAsync(instance with { State = RecordingState.Failed, Error = AppendError(error, "No transport data was captured.") }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SaveAsync(instance with { State = RecordingState.Finalizing, Error = error }, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await finalizer.FinalizeAsync(instance.SourcePath, instance.OutputPath, channel, retainSource || partial, cancellationToken).ConfigureAwait(false);
            await SaveAsync(instance with { State = partial ? RecordingState.Partial : RecordingState.Completed, OutputPath = result.OutputPath, Error = AppendError(error, result.Warning) }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            await SaveAsync(instance with { State = RecordingState.Partial, Error = AppendError(error, "Finalization failed; transport data was retained: " + exception.Message) }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        foreach (var instance in await repository.GetRecordingsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (active.ContainsKey(instance.Id) || instance.State is not (RecordingState.Recording or RecordingState.Finalizing))
            {
                continue;
            }

            var channel = await repository.GetChannelAsync(instance.ChannelId, cancellationToken).ConfigureAwait(false);
            if (channel is null)
            {
                await SaveAsync(instance with { State = RecordingState.Failed, Error = "The interrupted recording channel is no longer configured." }, cancellationToken).ConfigureAwait(false);
            }
            else if (instance.State == RecordingState.Recording && instance.End > clock.GetUtcNow())
            {
                await SaveAsync(instance with { State = RecordingState.Scheduled, Error = "Recording was interrupted and resumed; the reception gap is retained in its status." }, cancellationToken).ConfigureAwait(false);
            }
            else if (instance.SourcePath is not null && File.Exists(instance.SourcePath))
            {
                await FinalizeCaptureAsync(instance, channel, retainSource: true, partial: instance.State == RecordingState.Recording || instance.Error is not null,
                    AppendError(instance.Error, "Recovered after application restart."), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RecoverPublishedOutputAsync(instance, channel, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RecoverPublishedOutputAsync(RecordingInstance instance, TvChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            if (instance.OutputPath is null)
            {
                throw new InvalidDataException("The interrupted recording has no output path.");
            }

            await finalizer.ValidateAsync(instance.OutputPath, channel.Kind, cancellationToken).ConfigureAwait(false);
            await SaveAsync(instance with { State = RecordingState.Completed, Error = "Recovered a validated recording published before application restart." }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            await SaveAsync(instance with { State = RecordingState.Failed, Error = "No valid recording could be recovered: " + exception.Message }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task UpdateGuideTimersAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var guide = await repository.GetProgrammesAsync(now.AddDays(-1), now.AddDays(14), cancellationToken).ConfigureAwait(false);
        var instances = await repository.GetRecordingsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var definition in await repository.GetDefinitionsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (definition.EventId is null || definition.RepeatOn != Weekdays.None || instances.Any(item => item.DefinitionId == definition.Id && item.State is not (RecordingState.Scheduled or RecordingState.Conflict)))
            {
                continue;
            }

            var channel = await repository.GetChannelAsync(definition.ChannelId, cancellationToken).ConfigureAwait(false);
            if (channel is null)
            {
                continue;
            }

            var programme = guide.Where(item => item.EventId == definition.EventId && ProgrammeMatchesChannel(item, channel)
                && Math.Abs((item.Start - definition.Start).TotalHours) < 12).OrderBy(item => Math.Abs((item.Start - definition.Start).TotalHours)).FirstOrDefault();
            if (programme is null || (programme.Start == definition.Start && programme.End == definition.End))
            {
                continue;
            }

            await repository.SaveDefinitionAsync(definition with { Start = programme.Start, End = programme.End }, cancellationToken).ConfigureAwait(false);
            foreach (var instance in instances.Where(item => item.DefinitionId == definition.Id && item.State is RecordingState.Scheduled or RecordingState.Conflict))
            {
                await SaveAsync(instance with { Start = programme.Start - definition.EffectiveStartPadding, End = programme.End + definition.EffectiveEndPadding }, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool ProgrammeMatchesChannel(Programme programme, TvChannel channel) => channel.ServiceIdentity is { } identity
        ? programme.Service == identity
        : programme.Service.DeviceId == channel.DeviceId && programme.Service.ServiceId == channel.ServiceId;

    private async Task SaveAsync(RecordingInstance instance, CancellationToken cancellationToken)
    {
        await repository.SaveRecordingAsync(instance, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void CheckFreeSpace(AppSettings settings)
    {
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(settings.RecordingDirectory))!).AvailableFreeSpace < settings.FreeSpaceReserveBytes)
        {
            throw new IOException("The recording free-space reserve has been reached.");
        }
    }

    private static string CreateSourcePath(string directory, RecordingInstance instance)
    {
        const string forbidden = "<>:\"/\\|?*";
        var builder = new StringBuilder();
        foreach (var rune in instance.Title.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            var value = Rune.IsControl(rune) || (rune.IsAscii && forbidden.Contains((char)rune.Value, StringComparison.Ordinal)) ? "_" : rune.ToString();
            if (Encoding.UTF8.GetByteCount(builder.ToString()) + Encoding.UTF8.GetByteCount(value) > 100)
            {
                break;
            }

            builder.Append(value);
        }

        var title = builder.ToString().Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Recording";
        }

        var filename = string.Create(CultureInfo.InvariantCulture, $"{instance.Start:yyyyMMdd-HHmmss}-{title}-{instance.Id:N}.ts");
        return Path.Combine(Path.GetFullPath(directory), filename);
    }

    private static string? AppendError(string? previous, string? next)
    {
        if (string.IsNullOrWhiteSpace(next))
        {
            return previous;
        }

        return string.IsNullOrWhiteSpace(previous) ? next : previous + " " + next;
    }

    private sealed class CaptureJob(RecordingInstance instance, TvChannel channel, AppSettings settings, bool retainSource, bool partial)
    {
        private RecordingInstance current = instance;
        private int reconnecting;

        public RecordingInstance Instance { get => Volatile.Read(ref current); set => Volatile.Write(ref current, value); }

        public TvChannel Channel { get; } = channel;

        public AppSettings Settings { get; } = settings;

        public bool RetainSource { get; } = retainSource;

        public bool Partial { get; set; } = partial;

        public bool Interrupted { get; private set; }

        public bool Reconnecting { get => Volatile.Read(ref reconnecting) != 0; set => Volatile.Write(ref reconnecting, value ? 1 : 0); }

        public CancellationTokenSource Cancellation { get; } = new();

        public RecordingContinuityMonitor Continuity { get; } = new();

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion => Done.Task;

        public string? Error { get; set; }

        public int? ServiceId { get; set; }

        public void RequestStop(bool interrupted)
        {
            Interrupted |= interrupted;
            try { Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private sealed class ReceptionException(string message, Exception? inner = null) : IOException(message, inner);
}
