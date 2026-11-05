using FritzTV.Core.Models;
using FritzTV.Infrastructure.Recording;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class RecordingTests
{
    private const string SourceIdentification = """
        {"tracks":[
          {"id":0,"type":"audio","codec":"AAC","properties":{"program_number":99}},
          {"id":1,"type":"video","codec":"AVC","properties":{"program_number":3}},
          {"id":2,"type":"audio","codec":"AC3","properties":{"program_number":3}},
          {"id":3,"type":"audio","codec":"AC3","properties":{"program_number":3}},
          {"id":4,"type":"subtitles","codec":"SubRip/SRT","properties":{"program_number":3}},
          {"id":5,"type":"subtitles","codec":"DVBSUB","properties":{"program_number":3}}
        ],"errors":[]}
        """;

    private const string OutputIdentification = """
        {"tracks":[
          {"id":0,"type":"video","codec":"AVC","properties":{}},
          {"id":1,"type":"audio","codec":"AC3","properties":{}},
          {"id":2,"type":"audio","codec":"AC3","properties":{}},
          {"id":3,"type":"subtitles","codec":"SubRip/SRT","properties":{}},
          {"id":4,"type":"subtitles","codec":"DVBSUB","properties":{}}
        ],"container":{"type":"Matroska","properties":{"duration":2000000000}},"errors":[]}
        """;

    [Fact]
    public void SelectionPreservesAllProgrammeAudioAndBothSubtitleKinds()
    {
        var tracks = RecordingFinalizer.SelectProgrammeTracks(SourceIdentification, 3, ChannelKind.Television);
        Assert.Equal([1, 2, 3, 4, 5], tracks.Select(track => track.Id));
        Assert.Equal(2, tracks.Count(track => string.Equals(track.Type, "audio", StringComparison.Ordinal)));
        Assert.Equal(2, tracks.Count(track => string.Equals(track.Type, "subtitles", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => RecordingFinalizer.SelectProgrammeTracks(SourceIdentification, null, ChannelKind.Television));
        Assert.Equal(0, Assert.Single(RecordingFinalizer.SelectProgrammeTracks(SourceIdentification, 99, ChannelKind.Radio)).Id);
    }

    [Fact]
    public async Task SuccessfulFinalizationSelectsOneProgrammeAndPublishesAtomically()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "fritztv-remux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.ts");
            var output = Path.Combine(directory, "output.mkv");
            await File.WriteAllBytesAsync(source, new byte[188], cancellationToken);
            var runner = new RemuxRunner(SourceIdentification, OutputIdentification);
            var channel = new TvChannel("test", Guid.NewGuid(), "Test; harmless title", ChannelKind.Television, new Uri("rtsp://192.0.2.1/"), 3);
            var result = await new RecordingFinalizer("synthetic-mkvmerge", runner, 0).FinalizeAsync(source, output, channel, false, cancellationToken);
            Assert.True(File.Exists(output));
            Assert.False(File.Exists(source));
            Assert.False(result.SourceRetained);
            Assert.Equal("1", runner.RemuxArguments[runner.RemuxArguments.IndexOf("--video-tracks") + 1]);
            Assert.Equal("2,3", runner.RemuxArguments[runner.RemuxArguments.IndexOf("--audio-tracks") + 1]);
            Assert.Equal("4,5", runner.RemuxArguments[runner.RemuxArguments.IndexOf("--subtitle-tracks") + 1]);
            Assert.Empty(Directory.GetFiles(directory, "*.partial"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectedOutputKeepsRecoverableSourceAndDoesNotPublish()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "fritztv-remux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.ts");
            var output = Path.Combine(directory, "output.mkv");
            await File.WriteAllBytesAsync(source, new byte[188], cancellationToken);
            var invalid = OutputIdentification.Replace("2000000000", "0", StringComparison.Ordinal);
            var runner = new RemuxRunner(SourceIdentification, invalid);
            var channel = new TvChannel("test", Guid.NewGuid(), "Test", ChannelKind.Television, new Uri("rtsp://192.0.2.1/"), 3);
            await Assert.ThrowsAsync<InvalidDataException>(() => new RecordingFinalizer("synthetic-mkvmerge", runner, 0).FinalizeAsync(source, output, channel, false, cancellationToken));
            Assert.True(File.Exists(source));
            Assert.False(File.Exists(output));
            Assert.Empty(Directory.GetFiles(directory, "*.partial"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingSubtitleCuesWarnAndKeepSourceWithoutRejectingValidAudioVideo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "fritztv-subtitles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.ts");
            var output = Path.Combine(directory, "output.mkv");
            await File.WriteAllBytesAsync(source, new byte[188], cancellationToken);
            using var document = System.Text.Json.JsonDocument.Parse(OutputIdentification);
            var tracks = document.RootElement.GetProperty("tracks").EnumerateArray().Where(track => !string.Equals(track.GetProperty("type").GetString(), "subtitles", StringComparison.Ordinal)).ToArray();
            var noCues = System.Text.Json.JsonSerializer.Serialize(new { tracks, container = new { type = "Matroska", properties = new { duration = 2000000000L } }, errors = Array.Empty<string>() });
            var channel = new TvChannel("test", Guid.NewGuid(), "Test", ChannelKind.Television, new Uri("rtsp://192.0.2.1/"), 3);
            var result = await new RecordingFinalizer("synthetic-mkvmerge", new RemuxRunner(SourceIdentification, noCues), 0).FinalizeAsync(source, output, channel, false, cancellationToken);
            Assert.True(File.Exists(output));
            Assert.True(File.Exists(source));
            Assert.True(result.SourceRetained);
            Assert.Contains("subtitle", result.Warning, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StopWaitsForFinalizationAndReleasesCapturePowerLease()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        scope.Finalizer.WaitForRelease = true;
        var settings = await scope.Repository.GetSettingsAsync(cancellationToken);
        var instance = await scope.Coordinator.StartInstantAsync(channel, settings, cancellationToken: cancellationToken);
        await scope.Inputs.Latest[channel.Id].Written.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(1, scope.Power.Active);
        var stopped = scope.Coordinator.StopAsync(instance.Id, cancellationToken);
        await scope.Finalizer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.False(stopped.IsCompleted);
        Assert.Equal(0, scope.Power.Active);
        scope.Finalizer.Release.TrySetResult();
        await stopped;
        var complete = Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken));
        Assert.Equal(RecordingState.Completed, complete.State);
        Assert.True(File.Exists(complete.OutputPath));
        Assert.Equal(0, scope.Inputs.Active);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task FinalizationFailureAndPacketLossPreserveTransport(bool invalidContainer, bool accessDenied)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        scope.Finalizer.Fail = !invalidContainer && !accessDenied;
        scope.Finalizer.InvalidOutput = invalidContainer;
        scope.Finalizer.AccessDenied = accessDenied;
        var instance = await scope.Coordinator.StartInstantAsync(channel, await scope.Repository.GetSettingsAsync(cancellationToken), cancellationToken: cancellationToken);
        var input = scope.Inputs.Latest[channel.Id];
        input.LostPackets = 4;
        await input.Written.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await scope.Coordinator.StopAsync(instance.Id, cancellationToken);
        var partial = Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken));
        Assert.Equal(RecordingState.Partial, partial.State);
        Assert.False(partial.NeedsRecovery);
        Assert.True(File.Exists(partial.SourcePath));
        Assert.Contains("4 RTP packets", partial.Error, StringComparison.Ordinal);
        Assert.Contains(accessDenied ? "Synthetic recording access denied" : invalidContainer ? "Synthetic invalid container" : "Synthetic remux failure", partial.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceptionRetryKeepsTheSourceAndPrecedesNewTimersOnThatDevice(bool priorityLeaseFailure)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        var other = channel with { Id = "waiting", Name = "Waiting programme" };
        await scope.Repository.SaveChannelsAsync([other], cancellationToken);
        scope.Inputs.Limit = 1;
        scope.Inputs.FailingInputs = 1;
        scope.Inputs.FailPriorityLease = priorityLeaseFailure;
        var instance = await scope.Coordinator.StartInstantAsync(channel, await scope.Repository.GetSettingsAsync(cancellationToken), cancellationToken: cancellationToken);
        var first = scope.Inputs.Latest[channel.Id];
        await first.Written.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await first.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var now = scope.Clock.GetUtcNow();
        await scope.Repository.SaveRecordingAsync(new RecordingInstance(Guid.NewGuid(), Guid.Empty, other.Id, "Waiting", now, now.AddHours(1), now, RecordingState.Scheduled), cancellationToken);
        await scope.Coordinator.RunDueAsync(cancellationToken);
        Assert.False(scope.Inputs.Latest.ContainsKey(other.Id));
        await scope.Inputs.Reopened.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await scope.Inputs.Latest[channel.Id].Written.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await scope.Coordinator.StopAsync(instance.Id, cancellationToken);
        var partial = (await scope.Coordinator.GetRecordingsAsync(cancellationToken)).Single(item => item.Id == instance.Id);
        Assert.Equal(RecordingState.Partial, partial.State);
        Assert.True(new FileInfo(partial.SourcePath!).Length >= 2 * 188 * 512);
        Assert.Contains(priorityLeaseFailure ? "recording-priority" : "Synthetic transport interruption", partial.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShutdownPreparationFlushesPartialTransportWithoutStartingFinalizationOrNewTimers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        var instance = await scope.Coordinator.StartInstantAsync(channel, await scope.Repository.GetSettingsAsync(cancellationToken), cancellationToken: cancellationToken);
        await scope.Inputs.Latest[channel.Id].Written.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await scope.Coordinator.PrepareShutdownAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var partial = Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken));
        Assert.Equal(RecordingState.Partial, partial.State);
        Assert.True(partial.NeedsRecovery);
        Assert.Equal(188 * 512, new FileInfo(partial.SourcePath!).Length);
        Assert.False(scope.Finalizer.Entered.Task.IsCompleted);
        Assert.Equal(0, scope.Power.Active);
        Assert.Equal(0, scope.Inputs.Active);
        var now = scope.Clock.GetUtcNow();
        await scope.Repository.SaveRecordingAsync(new RecordingInstance(Guid.NewGuid(), Guid.Empty, channel.Id, "Due during shutdown", now, now.AddHours(1), now, RecordingState.Scheduled), cancellationToken);
        await scope.Coordinator.RunDueAsync(cancellationToken);
        Assert.Single(scope.Inputs.Attempts);
    }

    [Fact]
    public async Task ManuallyStoppedPartialWithRemainingTimeIsTerminalOnRecovery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        var instance = await scope.Coordinator.StartInstantAsync(channel, await scope.Repository.GetSettingsAsync(cancellationToken), until: scope.Clock.GetUtcNow().AddHours(1), cancellationToken: cancellationToken);
        var input = scope.Inputs.Latest[channel.Id];
        input.LostPackets = 1;
        await input.Written.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await scope.Coordinator.StopAsync(instance.Id, cancellationToken);
        await scope.Coordinator.RunDueAsync(cancellationToken);
        var stopped = Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken));
        Assert.Equal(RecordingState.Partial, stopped.State);
        Assert.False(stopped.NeedsRecovery);
        Assert.Single(scope.Inputs.Attempts);
        Assert.Single(scope.Finalizer.Retained);
    }

    [Fact]
    public async Task HealthyWritesResetTheBudgetAcrossFourSeparateReceptionOutages()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        scope.Inputs.FailingInputs = 4;
        scope.Inputs.HealthyInterval = TimeSpan.FromSeconds(31);
        var instance = await scope.Coordinator.StartInstantAsync(channel, await scope.Repository.GetSettingsAsync(cancellationToken), cancellationToken: cancellationToken);
        await scope.Inputs.HealthyOpened.Task.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken);
        await scope.Inputs.Latest[channel.Id].Written.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await scope.Coordinator.StopAsync(instance.Id, cancellationToken);
        Assert.Equal(5, scope.Inputs.Attempts.Count);
        var partial = Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken));
        Assert.Equal(RecordingState.Partial, partial.State);
        Assert.True(new FileInfo(partial.SourcePath!).Length >= 5 * 188 * 512);
    }

    [Fact]
    public async Task ConsecutiveFailuresWithoutDataStillExhaustTheBoundedRetryBudget()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        scope.Inputs.FailingInputs = 10;
        scope.Inputs.FailBeforeWriting = true;
        _ = await scope.Coordinator.StartInstantAsync(channel, await scope.Repository.GetSettingsAsync(cancellationToken), cancellationToken: cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        while (Assert.Single(await scope.Coordinator.GetRecordingsAsync(deadline.Token)).State != RecordingState.Failed)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
        }

        Assert.Equal(4, scope.Inputs.Attempts.Count);
        Assert.False(scope.Inputs.HealthyOpened.Task.IsCompleted);
        Assert.Equal(0, scope.Power.Active);
    }

    [Fact]
    public async Task InhibitionFailureKeepsHealthyCaptureCompletedWithASeparateWarning()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        scope.Power.Fail = true;
        var instance = await scope.Coordinator.StartInstantAsync(channel, await scope.Repository.GetSettingsAsync(cancellationToken), cancellationToken: cancellationToken);
        await scope.Inputs.Latest[channel.Id].Written.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Contains("Synthetic inhibitor unavailable", instance.Warning, StringComparison.Ordinal);
        Assert.Null(instance.Error);
        await scope.Coordinator.StopAsync(instance.Id, cancellationToken);
        var completed = Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken));
        Assert.Equal(RecordingState.Completed, completed.State);
        Assert.Null(completed.Error);
        Assert.NotNull(completed.Warning);
        Assert.True(File.Exists(completed.OutputPath));
        Assert.False(File.Exists(completed.SourcePath));
        Assert.False(Assert.Single(scope.Finalizer.Retained));
    }

    [Fact]
    public void ContinuityMonitorDetectsHistoricalBufferGapsButAllowsWrapAndSignaledReset()
    {
        var monitor = new RecordingContinuityMonitor();
        for (var counter = 0; counter <= 16; counter++)
        {
            monitor.Inspect(TransportPacket(counter & 15));
        }

        Assert.Equal(0, monitor.Problems);
        var lost = TransportPacket(2);
        monitor.Inspect(lost.AsSpan(0, 100));
        monitor.Inspect(lost.AsSpan(100));
        Assert.Equal(1, monitor.Problems);
        monitor.Inspect(lost);
        Assert.Equal(1, monitor.Problems);
        monitor.Inspect(TransportPacket(8, discontinuity: true));
        monitor.Inspect(TransportPacket(9));
        Assert.Equal(1, monitor.Problems);
    }

    private static byte[] TransportPacket(int counter, bool discontinuity = false)
    {
        var packet = new byte[188];
        packet.AsSpan().Fill(0xff);
        packet[0] = 0x47;
        packet[1] = 0;
        packet[2] = 100;
        packet[3] = (byte)((discontinuity ? 0x30 : 0x10) | counter);
        if (discontinuity)
        {
            packet[4] = 1;
            packet[5] = 0x80;
        }

        return packet;
    }

    private sealed class RemuxRunner(string source, string output) : IMediaProcessRunner
    {
        public List<string> RemuxArguments { get; private set; } = [];

        public async Task<MediaProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            if (arguments.Contains("--identify", StringComparer.Ordinal))
            {
                return new MediaProcessResult(0, arguments[^1].EndsWith(".ts", StringComparison.Ordinal) ? source : output, string.Empty);
            }

            RemuxArguments = arguments.ToList();
            var path = arguments[RemuxArguments.IndexOf("--output") + 1];
            await File.WriteAllBytesAsync(path, new byte[512], cancellationToken).ConfigureAwait(false);
            return new MediaProcessResult(0, string.Empty, string.Empty);
        }
    }
}
