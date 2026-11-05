using FritzTV.Core.Models;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class RecordingIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturePersistsTheSourceProgrammeBeforeAMutableChannelRefresh(bool discoverAfterOpen)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        scope.Inputs.ServiceId = discoverAfterOpen ? null : 3;
        var instance = await scope.Coordinator.StartInstantAsync(channel, await scope.Repository.GetSettingsAsync(cancellationToken), cancellationToken: cancellationToken);
        await scope.Inputs.Latest[channel.Id].Written.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(3, Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken)).ServiceId);
        await scope.Repository.SaveChannelsAsync([channel with { ServiceId = 99 }], cancellationToken);
        await scope.Coordinator.StopAsync(instance.Id, cancellationToken);
        Assert.Equal(3, Assert.Single(scope.Finalizer.ServiceIds));
        Assert.Equal(3, Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken)).ServiceId);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(false, null)]
    [InlineData(true, null)]
    public async Task RetryAndRecoveryUseTheCapturedIdentityOrProbeALegacySource(bool recover, int? serviceId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        await scope.Repository.SaveChannelsAsync([channel with { ServiceId = 99 }], cancellationToken);
        var now = scope.Clock.GetUtcNow();
        var source = Path.Combine(scope.DirectoryPath, "retained.ts");
        await File.WriteAllBytesAsync(source, new byte[188], cancellationToken);
        var instance = new RecordingInstance(Guid.NewGuid(), Guid.Empty, channel.Id, "Retained capture", now.AddMinutes(-1), now.AddSeconds(-1), now.AddMinutes(-2),
            RecordingState.Partial, source, Path.ChangeExtension(source, ".mkv"), NeedsRecovery: recover, ServiceId: serviceId);
        await scope.Repository.SaveRecordingAsync(instance, cancellationToken);
        if (recover)
        {
            await scope.Coordinator.RunDueAsync(cancellationToken);
        }
        else
        {
            await scope.Coordinator.RetryFinalizeAsync(instance.Id, cancellationToken);
        }

        Assert.Equal(serviceId, Assert.Single(scope.Finalizer.ServiceIds));
        var result = Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken));
        Assert.Equal(serviceId, result.ServiceId);
        Assert.Equal(RecordingState.Partial, result.State);
        Assert.True(File.Exists(result.OutputPath));
    }
}
