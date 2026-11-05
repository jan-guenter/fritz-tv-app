using FritzTV.Core.Models;
using FritzTV.Infrastructure.Scheduling;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class SchedulerTests
{
    [Fact]
    public async Task GlobalPaddingDefaultsApplyToNewAndLegacyTimersWithoutDuplicatingOccurrences()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        var settings = await scope.Repository.GetSettingsAsync(cancellationToken);
        await scope.Repository.SaveSettingsAsync(settings with { DefaultStartPadding = TimeSpan.FromMinutes(4), DefaultEndPadding = TimeSpan.FromMinutes(8) }, cancellationToken);
        var start = scope.Clock.GetUtcNow().AddHours(1);
        var legacy = new RecordingDefinition(Guid.NewGuid(), channel.Id, "Legacy", start, start.AddHours(1), "UTC");
        await scope.Repository.SaveDefinitionAsync(legacy, cancellationToken);
        var old = new RecordingInstance(Guid.NewGuid(), legacy.Id, channel.Id, legacy.Title, start.AddMinutes(-2), start.AddHours(1).AddMinutes(5), scope.Clock.GetUtcNow(), RecordingState.Scheduled);
        await scope.Repository.SaveRecordingAsync(old, cancellationToken);
        var created = legacy with { Id = Guid.NewGuid(), Title = "New", Start = start.AddDays(1), End = start.AddDays(1).AddHours(1) };
        var fresh = Assert.Single(await scope.Coordinator.CreateTimerAsync(created, cancellationToken));
        Assert.Equal(created.Start.AddMinutes(-4), fresh.Start);
        Assert.Equal(created.End.AddMinutes(8), fresh.End);
        await scope.Coordinator.RunDueAsync(cancellationToken);
        var restored = Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken), item => item.DefinitionId == legacy.Id);
        Assert.Equal(old.Id, restored.Id);
        Assert.Equal(start.AddMinutes(-4), restored.Start);
        Assert.Equal(start.AddHours(1).AddMinutes(8), restored.End);
        var definitions = await scope.Repository.GetDefinitionsAsync(cancellationToken);
        Assert.All(definitions, item => Assert.Equal(TimeSpan.FromMinutes(4), item.StartPadding));
        Assert.All(definitions, item => Assert.Equal(TimeSpan.FromMinutes(8), item.EndPadding));
    }

    [Fact]
    public async Task RepeatedTicksDoNotDuplicateFutureRecurringInstances()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        var start = scope.Clock.GetUtcNow().AddDays(1);
        var definition = Timer(channel, start, start.AddHours(1)) with { RepeatOn = Weekdays.EveryDay };
        await scope.Coordinator.CreateTimerAsync(definition, cancellationToken);
        await scope.Coordinator.RunDueAsync(cancellationToken);
        var first = await scope.Coordinator.GetRecordingsAsync(cancellationToken);
        await scope.Coordinator.RunDueAsync(cancellationToken);
        var second = await scope.Coordinator.GetRecordingsAsync(cancellationToken);
        Assert.True(first.Count > 5);
        Assert.Equal(first.Select(item => item.Id).Order(), second.Select(item => item.Id).Order());
        Assert.Equal(0, scope.Inputs.Active);
        await scope.Coordinator.CancelTimerAsync(definition.Id, cancellationToken);
        Assert.Empty(await scope.Repository.GetDefinitionsAsync(cancellationToken));
        Assert.Empty(await scope.Coordinator.GetRecordingsAsync(cancellationToken));
    }

    [Fact]
    public async Task ElapsedTimerIsMissedWithoutOpeningATuner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        var now = scope.Clock.GetUtcNow();
        await scope.Coordinator.CreateTimerAsync(Timer(channel, now.AddHours(-2), now.AddHours(-1)), cancellationToken);
        await scope.Coordinator.RunDueAsync(cancellationToken);
        Assert.Equal(RecordingState.Missed, Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken)).State);
        Assert.Empty(scope.Inputs.Attempts);
    }

    [Fact]
    public async Task EarlierCreatedRecordingWinsAndConflictRetriesWithoutPreemption()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var firstChannel = await scope.InitializeAsync(cancellationToken);
        var secondChannel = firstChannel with { Id = "second", Name = "Other synthetic programme", StreamUri = new Uri("rtsp://192.0.2.1/?freq=200&pids=0,200,201") };
        await scope.Repository.SaveChannelsAsync([secondChannel], cancellationToken);
        scope.Inputs.Limit = 1;
        var now = scope.Clock.GetUtcNow();
        var first = Instance(firstChannel, now, now.AddMinutes(-1));
        var second = Instance(secondChannel, now, now);
        await scope.Repository.SaveRecordingAsync(second, cancellationToken);
        await scope.Repository.SaveRecordingAsync(first, cancellationToken);
        await scope.Coordinator.RunDueAsync(cancellationToken);
        await scope.Inputs.Latest[firstChannel.Id].Written.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var states = await scope.Coordinator.GetRecordingsAsync(cancellationToken);
        Assert.Equal(RecordingState.Recording, states.Single(item => item.Id == first.Id).State);
        Assert.Equal(RecordingState.Conflict, states.Single(item => item.Id == second.Id).State);
        Assert.Equal(1, scope.Inputs.Active);
        Assert.Equal(firstChannel.Id, scope.Inputs.Attempts.First());
        await scope.Coordinator.StopAsync(first.Id, cancellationToken);
        await scope.Coordinator.RunDueAsync(cancellationToken);
        await scope.Inputs.Latest[secondChannel.Id].Written.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(RecordingState.Recording, (await scope.Coordinator.GetRecordingsAsync(cancellationToken)).Single(item => item.Id == second.Id).State);
        await scope.Coordinator.StopAsync(second.Id, cancellationToken);
    }

    [Fact]
    public async Task GuideTimerMovesBeforeStartButItsEndFreezesDuringCapture()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        var start = scope.Clock.GetUtcNow().AddMinutes(20);
        var definition = Timer(channel, start, start.AddHours(1)) with { EventId = 42 };
        await scope.Coordinator.CreateTimerAsync(definition, cancellationToken);
        var programme = new Programme(new DvbServiceIdentity(channel.DeviceId, 1, 2, 3), 42, start.AddMinutes(10), TimeSpan.FromHours(1), "Moved programme", "Synthetic guide", "eng");
        await scope.Repository.SaveProgrammesAsync([programme], cancellationToken);
        await scope.Coordinator.RunDueAsync(cancellationToken);
        var moved = Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken));
        Assert.Equal(programme.Start, moved.Start);
        Assert.Equal(programme.End, moved.End);
        scope.Clock.Advance(TimeSpan.FromMinutes(30));
        await scope.Coordinator.RunDueAsync(cancellationToken);
        await scope.Inputs.Latest[channel.Id].Written.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await scope.Repository.SaveProgrammesAsync([programme with { Start = programme.Start.AddMinutes(5) }], cancellationToken);
        scope.Clock.Advance(TimeSpan.FromSeconds(31));
        await scope.Coordinator.RunDueAsync(cancellationToken);
        var active = Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken));
        Assert.Equal(RecordingState.Recording, active.State);
        Assert.Equal(programme.End, active.End);
        await scope.Coordinator.StopAsync(active.Id, cancellationToken);
    }

    [Fact]
    public async Task InterruptedCurrentCaptureResumesAsPartialAndKeepsItsSource()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        var now = scope.Clock.GetUtcNow();
        var source = Path.Combine(scope.DirectoryPath, "interrupted.ts");
        await File.WriteAllBytesAsync(source, new byte[188], cancellationToken);
        var instance = Instance(channel, now.AddMinutes(-10), now.AddDays(-1)) with
        {
            State = RecordingState.Recording,
            SourcePath = source,
            OutputPath = Path.ChangeExtension(source, ".mkv"),
        };
        await scope.Repository.SaveRecordingAsync(instance, cancellationToken);
        await scope.Coordinator.RunDueAsync(cancellationToken);
        await scope.Inputs.Latest[channel.Id].Written.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await scope.Coordinator.StopAsync(instance.Id, cancellationToken);
        var recovered = Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken));
        Assert.Equal(RecordingState.Partial, recovered.State);
        Assert.True(File.Exists(source));
        Assert.True(new FileInfo(source).Length > 188);
        Assert.True(Assert.Single(scope.Finalizer.Retained));
        Assert.Contains("interrupted", recovered.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GracefulPartialFiniteCaptureResumesRemainingIntervalAndClearsItsMarker()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        var now = scope.Clock.GetUtcNow();
        string source = Path.Combine(scope.DirectoryPath, "graceful.ts");
        await File.WriteAllBytesAsync(source, new byte[188], cancellationToken);
        var instance = Instance(channel, now.AddMinutes(-10), now.AddDays(-1)) with
        {
            State = RecordingState.Partial,
            NeedsRecovery = true,
            SourcePath = source,
            OutputPath = Path.ChangeExtension(source, ".mkv"),
        };
        await scope.Repository.SaveRecordingAsync(instance, cancellationToken);
        await scope.Coordinator.RunDueAsync(cancellationToken);
        await scope.Inputs.Latest[channel.Id].Written.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await scope.Coordinator.StopAsync(instance.Id, cancellationToken);
        var recovered = Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken));
        Assert.Equal(RecordingState.Partial, recovered.State);
        Assert.False(recovered.NeedsRecovery);
        Assert.True(new FileInfo(source).Length > 188);
    }

    [Fact]
    public async Task RestartedGuideCaptureKeepsTheEndFrozenAtItsOriginalStart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        var now = scope.Clock.GetUtcNow();
        var definition = Timer(channel, now.AddMinutes(-10), now.AddMinutes(20)) with { EventId = 42 };
        await scope.Repository.SaveDefinitionAsync(definition, cancellationToken);
        string source = Path.Combine(scope.DirectoryPath, "guide-interrupted.ts");
        await File.WriteAllBytesAsync(source, new byte[188], cancellationToken);
        var instance = Instance(channel, definition.Start, now.AddDays(-1)) with
        {
            DefinitionId = definition.Id,
            End = definition.End,
            State = RecordingState.Partial,
            NeedsRecovery = true,
            SourcePath = source,
            OutputPath = Path.ChangeExtension(source, ".mkv"),
        };
        await scope.Repository.SaveRecordingAsync(instance, cancellationToken);
        var changed = new Programme(new DvbServiceIdentity(channel.DeviceId, 1, 2, 3), 42, definition.Start, TimeSpan.FromHours(2), "Extended after capture started", "Synthetic guide", "eng");
        await scope.Repository.SaveProgrammesAsync([changed], cancellationToken);
        await scope.Coordinator.RunDueAsync(cancellationToken);
        await scope.Inputs.Latest[channel.Id].Written.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(definition.End, Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken)).End);
        await scope.Coordinator.StopAsync(instance.Id, cancellationToken);
    }

    [Theory]
    [InlineData(RecordingState.Recording, false)]
    [InlineData(RecordingState.Partial, true)]
    [InlineData(RecordingState.Scheduled, false)]
    [InlineData(RecordingState.Conflict, false)]
    public async Task InterruptedUnboundedInstantCaptureFinalizesWithoutAutomaticallyRestarting(RecordingState state, bool needsRecovery)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        var now = scope.Clock.GetUtcNow();
        string source = Path.Combine(scope.DirectoryPath, "manual-interrupted.ts");
        await File.WriteAllBytesAsync(source, new byte[188], cancellationToken);
        var instance = Instance(channel, now.AddMinutes(-10), now.AddDays(-1)) with
        {
            State = state,
            NeedsRecovery = needsRecovery,
            End = DateTimeOffset.MaxValue,
            SourcePath = source,
            OutputPath = Path.ChangeExtension(source, ".mkv"),
        };
        await scope.Repository.SaveRecordingAsync(instance, cancellationToken);
        await scope.Coordinator.RunDueAsync(cancellationToken);
        await scope.Coordinator.RunDueAsync(cancellationToken);
        var recovered = Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken));
        Assert.Equal(RecordingState.Partial, recovered.State);
        Assert.False(recovered.NeedsRecovery);
        Assert.True(File.Exists(recovered.OutputPath));
        Assert.True(File.Exists(source));
        Assert.Empty(scope.Inputs.Attempts);
        Assert.Single(scope.Finalizer.Retained);
    }

    [Fact]
    public async Task InterruptedUnboundedRequestWithNoSourceNeverAutomaticallyAcquiresATuner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        var now = scope.Clock.GetUtcNow();
        var instance = Instance(channel, now.AddMinutes(-1), now) with { End = DateTimeOffset.MaxValue };
        await scope.Repository.SaveRecordingAsync(instance, cancellationToken);
        await scope.Coordinator.RunDueAsync(cancellationToken);
        await scope.Coordinator.RunDueAsync(cancellationToken);
        Assert.Equal(RecordingState.Failed, Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken)).State);
        Assert.Empty(scope.Inputs.Attempts);
        Assert.Empty(scope.Finalizer.Retained);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExpiredInterruptedPartialFinalizesWhileTerminalPartialRemainsTerminal(bool needsRecovery)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        var now = scope.Clock.GetUtcNow();
        string source = Path.Combine(scope.DirectoryPath, "finished-partial.ts");
        await File.WriteAllBytesAsync(source, new byte[188], cancellationToken);
        var instance = Instance(channel, now.AddHours(-2), now.AddDays(-1)) with
        {
            State = RecordingState.Partial,
            NeedsRecovery = needsRecovery,
            SourcePath = source,
            OutputPath = Path.ChangeExtension(source, ".mkv"),
        };
        await scope.Repository.SaveRecordingAsync(instance, cancellationToken);
        await scope.Coordinator.RunDueAsync(cancellationToken);
        var recovered = Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken));
        Assert.Equal(RecordingState.Partial, recovered.State);
        Assert.False(recovered.NeedsRecovery);
        Assert.Equal(needsRecovery, File.Exists(recovered.OutputPath));
        Assert.Empty(scope.Inputs.Attempts);
        Assert.Equal(needsRecovery ? 1 : 0, scope.Finalizer.Retained.Count);
    }

    [Fact]
    public async Task PublishedOutputSurvivesRestartBeforeCompletionWasPersisted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        var now = scope.Clock.GetUtcNow();
        var output = Path.Combine(scope.DirectoryPath, "published.mkv");
        await File.WriteAllBytesAsync(output, new byte[512], cancellationToken);
        var instance = Instance(channel, now.AddHours(-1), now.AddDays(-1)) with
        {
            State = RecordingState.Finalizing,
            SourcePath = Path.Combine(scope.DirectoryPath, "already-removed.ts"),
            OutputPath = output,
        };
        await scope.Repository.SaveRecordingAsync(instance, cancellationToken);
        await scope.Coordinator.RunDueAsync(cancellationToken);
        Assert.Equal(RecordingState.Completed, Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken)).State);
        Assert.True(File.Exists(output));
        Assert.Empty(scope.Inputs.Attempts);
    }

    [Fact]
    public void ForecastSharesAFrequencyAndReleasesCapacityAtExactEnd()
    {
        var now = new DateTimeOffset(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
        var device = new SatIpDevice(Guid.NewGuid(), "uuid:synthetic", "Synthetic tuner", new Uri("http://192.0.2.1/descriptor"), null, null, 1);
        var first = new TvChannel("a", device.Id, "A", ChannelKind.Television, new Uri("rtsp://192.0.2.1/?freq=100&pids=1"));
        var same = first with { Id = "b", StreamUri = new Uri("rtsp://192.0.2.1/?freq=100&pids=2") };
        var other = first with { Id = "c", StreamUri = new Uri("rtsp://192.0.2.1/?freq=200&pids=3") };
        var active = Instance(first, now, now) with { State = RecordingState.Recording };
        var shared = Instance(same, now, now.AddMinutes(-2));
        var conflict = Instance(other, now, now.AddMinutes(-1));
        var touching = Instance(other, now.AddHours(1), now);
        var conflicts = RecordingConflictPlanner.FindConflicts([active, shared, conflict, touching], [first, same, other], [device], now);
        Assert.Equal(conflict.Id, Assert.Single(conflicts));
    }

    private static RecordingDefinition Timer(TvChannel channel, DateTimeOffset start, DateTimeOffset end) => new(Guid.NewGuid(), channel.Id, "Synthetic timer", start, end, "UTC", StartPadding: TimeSpan.Zero, EndPadding: TimeSpan.Zero);

    private static RecordingInstance Instance(TvChannel channel, DateTimeOffset start, DateTimeOffset created) => new(Guid.NewGuid(), Guid.Empty, channel.Id, "Synthetic capture", start, start.AddHours(1), created, RecordingState.Scheduled);
}
