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
    private static readonly Action<ILogger, Guid, RecordingState, string, Exception?> LogRecordingProblem = LoggerMessage.Define<Guid, RecordingState, string>(LogLevel.Warning, new EventId(3, "RecordingProblem"), "Recording {RecordingId} entered {State}: {Reason}");
    private const int MaximumDiagnosticLength = 2048;
    private const int MaximumTrackedDefinitionFailures = 256;
    private static readonly Action<ILogger, string, string, Exception?> LogInvalidDefinition = LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(5, "InvalidStoredTimer"), "Stored timer {TimerId} was ignored: {Reason}");
    private static readonly Action<ILogger, Guid, Exception?> LogPowerWarning = LoggerMessage.Define<Guid>(LogLevel.Warning, new EventId(4, "SleepInhibitionUnavailable"), "Recording {RecordingId} continues without automatic sleep protection; keep the computer awake.");
    private readonly AppRepository repository;
    private readonly TimeProvider clock;
    private readonly IRecordingInputFactory inputs;
    private readonly IRecordingFinalizer finalizer;
    private readonly IPowerInhibitor power;
    private readonly ILogger<RecordingCoordinator> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Lazy<Task> shutdownPreparation;
    private readonly ConcurrentDictionary<Guid, CaptureJob> active = new();
    private readonly Dictionary<string, string> definitionFailures = new(StringComparer.Ordinal);
    private bool definitionFailureLimitReported;
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
        shutdownPreparation = new Lazy<Task>(PrepareShutdownCoreAsync);
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

            var settings = await repository.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            definition = ResolvePadding(definition with { ChannelId = channel.Id }, settings);
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
        ObjectDisposedException.ThrowIf(disposed != 0 || shutdown.IsCancellationRequested, this);
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
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
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
            if (shutdown.IsCancellationRequested)
            {
                return;
            }

            if (!recovered)
            {
                await MigrateLegacyPaddingAsync(cancellationToken).ConfigureAwait(false);
                await RecoverAsync(cancellationToken).ConfigureAwait(false);
                recovered = true;
            }

            if (clock.GetUtcNow() >= nextGuideUpdate)
            {
                await UpdateGuideTimersAsync(cancellationToken).ConfigureAwait(false);
                nextGuideUpdate = clock.GetUtcNow().AddSeconds(30);
            }

            var definitions = await ReadDefinitionsAsync(cancellationToken).ConfigureAwait(false);
            if (clock.GetUtcNow() >= nextMaterialization)
            {
                foreach (var definition in definitions)
                {
                    await TryMaterializeDefinitionAsync(definition, cancellationToken).ConfigureAwait(false);
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

    public Task PrepareShutdownAsync(CancellationToken cancellationToken = default) => shutdownPreparation.Value.WaitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await shutdownPreparation.Value.ConfigureAwait(false);
        shutdown.Dispose();
        gate.Dispose();
    }

    private async Task PrepareShutdownCoreAsync()
    {
        Task cancellation = shutdown.CancelAsync();
        foreach (var job in active.Values)
        {
            job.RequestStop(interrupted: true);
        }

        await cancellation.ConfigureAwait(false);
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        CaptureJob[] jobs;
        try
        {
            jobs = active.Values.ToArray();
            foreach (var job in jobs)
            {
                job.RequestStop(interrupted: true);
            }
        }
        finally
        {
            gate.Release();
        }

        await Task.WhenAll(jobs.Select(job => job.Completion)).ConfigureAwait(false);
    }

    private async Task MigrateLegacyPaddingAsync(CancellationToken cancellationToken)
    {
        var settings = await repository.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var definitions = await repository.GetDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var definition in definitions.Where(item => item.StartPadding is null || item.EndPadding is null))
        {
            var resolved = ResolvePadding(definition, settings);
            if (TryExpandDefinition(resolved, out _))
            {
                await repository.ResolveDefinitionPaddingAsync(resolved, definition.EffectiveStartPadding, definition.EffectiveEndPadding, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static RecordingDefinition ResolvePadding(RecordingDefinition definition, AppSettings settings) => definition with
    {
        StartPadding = definition.StartPadding ?? settings.DefaultStartPadding,
        EndPadding = definition.EndPadding ?? settings.DefaultEndPadding,
    };

    private async Task<IReadOnlyList<RecordingDefinition>> ReadDefinitionsAsync(CancellationToken cancellationToken)
    {
        var snapshot = await repository.GetDefinitionSnapshotAsync(cancellationToken).ConfigureAwait(false);
        PruneDefinitionFailures(snapshot);
        foreach (var failure in snapshot.Failures)
        {
            ReportDefinitionFailure(failure.Id, failure.Message, null);
        }

        return snapshot.Definitions;
    }

    private async Task TryMaterializeDefinitionAsync(RecordingDefinition definition, CancellationToken cancellationToken)
    {
        if (TryExpandDefinition(definition, out var expanded))
        {
            await MaterializeDefinitionAsync(definition, expanded, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryExpandDefinition(RecordingDefinition definition, out IReadOnlyList<RecordingInstance> expanded)
    {
        try
        {
            ValidateDefinition(definition);
            var now = clock.GetUtcNow();
            expanded = ScheduleCalculator.Expand(definition, now.AddDays(-31), now.AddDays(14), now);
            return true;
        }
        catch (Exception exception) when (IsDefinitionFailure(exception))
        {
            ReportDefinitionFailure(definition.Id.ToString(), exception.Message, exception);
            expanded = [];
            return false;
        }
    }

    private static void ValidateDefinition(RecordingDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.ChannelId) || string.IsNullOrWhiteSpace(definition.Title) || string.IsNullOrWhiteSpace(definition.TimeZoneId))
        {
            throw new ArgumentException("A stored timer requires a channel, title, and time zone.", nameof(definition));
        }

        _ = TimeZoneInfo.FindSystemTimeZoneById(definition.TimeZoneId);
    }

    private static bool IsDefinitionFailure(Exception exception) => exception is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException;

    private void PruneDefinitionFailures(RecordingDefinitionSnapshot snapshot)
    {
        var failedIds = snapshot.Failures.Select(item => item.Id.Length <= 128 ? item.Id : item.Id[..128]).ToHashSet(StringComparer.Ordinal);
        var definitions = snapshot.Definitions.ToDictionary(item => item.Id.ToString(), StringComparer.Ordinal);
        foreach (string id in definitionFailures.Keys.ToArray())
        {
            if (failedIds.Contains(id))
            {
                continue;
            }

            if (definitions.TryGetValue(id, out var definition))
            {
                try
                {
                    ValidateDefinition(definition);
                    _ = ScheduleCalculator.Expand(definition, clock.GetUtcNow().AddDays(-31), clock.GetUtcNow().AddDays(14), clock.GetUtcNow());
                }
                catch (Exception exception) when (IsDefinitionFailure(exception))
                {
                    continue;
                }
            }

            definitionFailures.Remove(id);
        }

        if (definitionFailures.Count < MaximumTrackedDefinitionFailures)
        {
            definitionFailureLimitReported = false;
        }
    }

    private void ReportDefinitionFailure(string id, string message, Exception? exception)
    {
        id = id.Length <= 128 ? id : id[..128];
        message = message.Length <= MaximumDiagnosticLength ? message : message[..MaximumDiagnosticLength];
        if (definitionFailures.TryGetValue(id, out string? previous) && string.Equals(previous, message, StringComparison.Ordinal))
        {
            return;
        }

        if (previous is null && definitionFailures.Count >= MaximumTrackedDefinitionFailures)
        {
            if (!definitionFailureLimitReported)
            {
                definitionFailureLimitReported = true;
                LogInvalidDefinition(logger, id, "Additional invalid timer diagnostics were suppressed; repair the previously reported stored timers.", null);
            }

            return;
        }

        definitionFailures[id] = message;
        LogInvalidDefinition(logger, id, message, exception);
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
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
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
        using var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        channel = channel with { ServiceId = instance.ServiceId ?? channel.ServiceId };
        Directory.CreateDirectory(settings.RecordingDirectory);
        CheckFreeSpace(settings);
        IRecordingInput? input = null;
        IAsyncDisposable? inhibition = null;
        FileStream? writer = null;
        CaptureJob? job = null;
        var added = false;
        try
        {
            input = await inputs.OpenAsync(channel, playback, from, startCancellation.Token).ConfigureAwait(false);
            (inhibition, string? warning) = await AcquireInhibitionAsync(instance.Id, startCancellation.Token).ConfigureAwait(false);
            instance = instance with { Warning = AppendError(instance.Warning, warning), ServiceId = instance.ServiceId ?? input.ServiceId };
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
            await SaveAsync(job.Instance, startCancellation.Token).ConfigureAwait(false);
            _ = ObserveCaptureAsync(job, input, inhibition, writer);
            return job.Instance;
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

    private async ValueTask<(IAsyncDisposable Inhibition, string? Warning)> AcquireInhibitionAsync(Guid recordingId, CancellationToken cancellationToken)
    {
        try
        {
            return (await power.AcquireAsync(cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            LogPowerWarning(logger, recordingId, exception);
            return (NoInhibition.Instance, "Automatic sleep protection is unavailable; keep the computer awake during capture. " + exception.Message);
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
        var serviceId = job.Instance.ServiceId ?? input.ServiceId;
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
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            job.Partial = true;
            error = AppendError(error, exception.Message);
        }
        finally
        {
            try
            {
                job.Instance = job.Instance with { ServiceId = job.Instance.ServiceId ?? job.ServiceId ?? serviceId };
                if (job.Interrupted || shutdown.IsCancellationRequested)
                {
                    await SaveAsync(job.Instance with { State = RecordingState.Partial, NeedsRecovery = true, Error = AppendError(error, "Capture stopped when the application exited; transport data was retained.") }, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await FinalizeCaptureAsync(job.Instance, job.Channel, job.RetainSource, job.Partial, error, CancellationToken.None).ConfigureAwait(false);
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
        var retries = new RecordingReconnectBudget(clock);
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
                    await WriteInputAsync(job, input, writer, retries.Delivered).ConfigureAwait(false);
                    serviceId = input.ServiceId ?? serviceId;
                    if (input.LostPackets > 0)
                    {
                        job.Partial = true;
                        error = AppendError(error, input.LostPackets.ToString(CultureInfo.InvariantCulture) + " RTP packets were lost.");
                    }

                    return (serviceId, error);
                }
                catch (ReceptionException exception) when (retries.CanRetry && !job.Cancellation.IsCancellationRequested)
                {
                    job.Partial = true;
                    job.Reconnecting = true;
                    error = AppendError(error, exception.Message);
                    job.Error = error;
                    TimeSpan delay = retries.Failed();
                    if (input is not null)
                    {
                        serviceId = input.ServiceId ?? serviceId;
                        await input.DisposeAsync().ConfigureAwait(false);
                        input = null;
                    }

                    await Task.Delay(delay, clock, job.Cancellation.Token).ConfigureAwait(false);
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
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            throw new ReceptionException("Recording reconnection failed: " + exception.Message, exception);
        }
    }

    private async Task WriteInputAsync(CaptureJob job, IRecordingInput input, Stream writer, Action onDataReceived)
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

            await PersistCaptureServiceIdAsync(job, input.ServiceId).ConfigureAwait(false);
            job.Continuity.Inspect(iterator.Current.Span);
            await writer.WriteAsync(iterator.Current, job.Cancellation.Token).ConfigureAwait(false);
            if (!iterator.Current.IsEmpty)
            {
                onDataReceived();
            }
        }

        if (!job.Cancellation.IsCancellationRequested && clock.GetUtcNow() < job.Instance.End)
        {
            throw new ReceptionException("The tuner stream ended before recording was stopped.");
        }
    }

    private async Task PersistCaptureServiceIdAsync(CaptureJob job, int? serviceId)
    {
        if (serviceId is null || job.Instance.ServiceId == serviceId)
        {
            return;
        }

        await gate.WaitAsync(job.Cancellation.Token).ConfigureAwait(false);
        try
        {
            if (job.Instance.ServiceId is not null && job.Instance.ServiceId != serviceId)
            {
                throw new ReceptionException("The tuner changed the captured programme identity; the original transport source was retained.");
            }

            job.Instance = job.Instance with { ServiceId = serviceId };
            await SaveAsync(job.Instance, job.Cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
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
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Threading.Channels.ChannelClosedException)
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
        catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or System.Threading.Channels.ChannelClosedException)
        {
            // An async iterator must leave MoveNext before its DisposeAsync may run.
        }
    }

    private async Task FinalizeCaptureAsync(RecordingInstance instance, TvChannel channel, bool retainSource, bool partial, string? error, CancellationToken cancellationToken)
    {
        instance = instance with { NeedsRecovery = false };
        channel = channel with { ServiceId = instance.ServiceId };
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
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            await SaveAsync(instance with { State = RecordingState.Partial, Error = AppendError(error, "Finalization failed; transport data was retained: " + exception.Message) }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        foreach (var instance in await repository.GetRecordingsAsync(cancellationToken).ConfigureAwait(false))
        {
            bool pendingManualInstant = instance.DefinitionId == Guid.Empty && instance.End == DateTimeOffset.MaxValue && instance.State is RecordingState.Scheduled or RecordingState.Conflict;
            if (active.ContainsKey(instance.Id) || (instance.State is not (RecordingState.Recording or RecordingState.Finalizing) && !(instance.State == RecordingState.Partial && instance.NeedsRecovery) && !pendingManualInstant))
            {
                continue;
            }

            var channel = await repository.GetChannelAsync(instance.ChannelId, cancellationToken).ConfigureAwait(false);
            if (channel is null)
            {
                await SaveAsync(instance with { State = RecordingState.Failed, NeedsRecovery = false, Error = "The interrupted recording channel is no longer configured." }, cancellationToken).ConfigureAwait(false);
            }
            else if (instance.State != RecordingState.Finalizing && instance.End != DateTimeOffset.MaxValue && instance.End > clock.GetUtcNow())
            {
                await SaveAsync(instance with { State = RecordingState.Scheduled, NeedsRecovery = false, Error = "Recording was interrupted and resumed; the reception gap is retained in its status." }, cancellationToken).ConfigureAwait(false);
            }
            else if (instance.SourcePath is not null && File.Exists(instance.SourcePath))
            {
                await FinalizeCaptureAsync(instance, channel, retainSource: true, partial: pendingManualInstant || instance.NeedsRecovery || instance.State == RecordingState.Recording || instance.Error is not null,
                    AppendError(instance.Error, "Recovered after application restart."), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RecoverPublishedOutputAsync(instance, channel, pendingManualInstant, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RecoverPublishedOutputAsync(RecordingInstance instance, TvChannel channel, bool interruptedManualRequest, CancellationToken cancellationToken)
    {
        try
        {
            if (instance.OutputPath is null)
            {
                throw new InvalidDataException("The interrupted recording has no output path.");
            }

            await finalizer.ValidateAsync(instance.OutputPath, channel.Kind, cancellationToken).ConfigureAwait(false);
            await SaveAsync(instance with { State = interruptedManualRequest || instance.NeedsRecovery || instance.State == RecordingState.Recording || instance.Error is not null ? RecordingState.Partial : RecordingState.Completed, NeedsRecovery = false, Error = "Recovered a validated recording published before application restart." }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            await SaveAsync(instance with { State = RecordingState.Failed, NeedsRecovery = false, Error = "No valid recording could be recovered: " + exception.Message }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task UpdateGuideTimersAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var guide = await repository.GetProgrammesAsync(now.AddDays(-1), now.AddDays(14), cancellationToken).ConfigureAwait(false);
        var instances = await repository.GetRecordingsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var definition in await repository.GetDefinitionsAsync(cancellationToken).ConfigureAwait(false))
        {
            await UpdateGuideTimerAsync(definition, guide, instances, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task UpdateGuideTimerAsync(RecordingDefinition definition, IReadOnlyList<Programme> guide, IReadOnlyList<RecordingInstance> instances, CancellationToken cancellationToken)
    {
        if (!TryExpandDefinition(definition, out _) || definition.EventId is null || definition.RepeatOn != Weekdays.None || instances.Any(item => item.DefinitionId == definition.Id && (item.SourcePath is not null || item.State is not (RecordingState.Scheduled or RecordingState.Conflict))))
        {
            return;
        }

        var channel = await repository.GetChannelAsync(definition.ChannelId, cancellationToken).ConfigureAwait(false);
        if (channel is null)
        {
            return;
        }

        var programme = guide.Where(item => item.EventId == definition.EventId && ProgrammeMatchesChannel(item, channel)
            && Math.Abs((item.Start - definition.Start).TotalHours) < 12).OrderBy(item => Math.Abs((item.Start - definition.Start).TotalHours)).FirstOrDefault();
        if (programme is null || (programme.Start == definition.Start && programme.End == definition.End))
        {
            return;
        }

        var updated = definition with { Start = programme.Start, End = programme.End };
        if (TryExpandDefinition(updated, out _))
        {
            await repository.UpdateDefinitionTimingAsync(updated, cancellationToken).ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool ProgrammeMatchesChannel(Programme programme, TvChannel channel) => channel.ServiceIdentity is { } identity
        ? programme.Service == identity
        : programme.Service.DeviceId == channel.DeviceId && programme.Service.ServiceId == channel.ServiceId;

    private async Task SaveAsync(RecordingInstance instance, CancellationToken cancellationToken)
    {
        await repository.SaveRecordingAsync(instance, cancellationToken).ConfigureAwait(false);
        if (instance.Error is { } error && instance.State is RecordingState.Failed or RecordingState.Partial or RecordingState.Missed)
        {
            LogRecordingProblem(logger, instance.Id, instance.State, error, null);
        }

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
            return previous is not null && previous.Length > MaximumDiagnosticLength ? previous[..MaximumDiagnosticLength] : previous;
        }

        next = next.Length <= MaximumDiagnosticLength / 2 ? next : next[..(MaximumDiagnosticLength / 2)];
        if (string.IsNullOrWhiteSpace(previous))
        {
            return next;
        }

        previous = previous.Length <= MaximumDiagnosticLength ? previous : previous[..MaximumDiagnosticLength];
        if (previous.Contains(next, StringComparison.Ordinal))
        {
            return previous;
        }

        int available = MaximumDiagnosticLength - next.Length - 3;
        return previous.Length <= available ? previous + " " + next : previous[..available] + " … " + next;
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

    private sealed class NoInhibition : IAsyncDisposable
    {
        public static NoInhibition Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ReceptionException(string message, Exception? inner = null) : IOException(message, inner);
}
