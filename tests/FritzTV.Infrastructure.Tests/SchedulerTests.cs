using FritzTV.Core.Models;
using FritzTV.Infrastructure.Scheduling;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class SchedulerTests
{
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
