using FritzTV.Core.Models;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class RecordingDiagnosticsTests
{
    [Fact]
    public async Task BriefDataBetweenFailuresDoesNotRearmRetriesOrGrowRepeatedDiagnostics()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var cancellationToken = deadline.Token;
        await using var scope = new RecordingTestScope();
        var channel = await scope.InitializeAsync(cancellationToken);
        const string cause = "Synthetic repeated transport interruption.";
        scope.Inputs.FailingInputs = 10;
        scope.Inputs.HealthyInterval = TimeSpan.Zero;
        scope.Inputs.FailureMessage = cause + new string('x', 20000);
        var instance = await scope.Coordinator.StartInstantAsync(channel, await scope.Repository.GetSettingsAsync(cancellationToken), cancellationToken: cancellationToken);
        RecordingInstance result;
        do
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
            result = Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken), recording => recording.Id == instance.Id);
        }
        while (result.State is RecordingState.Scheduled or RecordingState.Recording or RecordingState.Finalizing);

        Assert.Equal(RecordingState.Partial, result.State);
        Assert.Equal(4, scope.Inputs.Attempts.Count);
        Assert.Equal(0, scope.Inputs.Active);
        Assert.True(File.Exists(result.SourcePath));
        Assert.NotNull(result.Error);
        Assert.InRange(result.Error.Length, 1, 2048);
        Assert.Equal(2, result.Error.Split(cause, StringSplitOptions.None).Length);
    }
}
