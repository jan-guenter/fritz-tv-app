using System.IO.Pipes;
using FritzTV.Desktop.Services;
using Xunit;

namespace FritzTV.Desktop.Tests;

public sealed class SingleInstanceActivationTests
{
    [Fact]
    public async Task ASecondLaunchActivatesThePrimaryAfterItsWindowHandlerIsReady()
    {
        string name = "FritzTV.Test." + Guid.NewGuid().ToString("N");
        await using var primary = await SingleInstanceActivation.CreateOrActivateAsync(name, TestContext.Current.CancellationToken);
        Assert.NotNull(primary);
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ActivationRequested += (_, _) => activated.TrySetResult();
        Task<SingleInstanceActivation?> second = SingleInstanceActivation.CreateOrActivateAsync(name, TestContext.Current.CancellationToken);
        primary.StartListening();
        Assert.Null(await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReleasingThePrimaryAllowsAFreshInstanceAndDifferentApplicationsAreIndependent()
    {
        string name = "FritzTV.Test." + Guid.NewGuid().ToString("N");
        var first = await SingleInstanceActivation.CreateOrActivateAsync(name, TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        await using var independent = await SingleInstanceActivation.CreateOrActivateAsync(name + ".other", TestContext.Current.CancellationToken);
        Assert.NotNull(independent);
        await first.DisposeAsync();
        await using var replacement = await SingleInstanceActivation.CreateOrActivateAsync(name, TestContext.Current.CancellationToken);
        Assert.NotNull(replacement);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAlreadyConnectedSecondaryCanAbandonWithoutPoisoningLaterRequests(bool sendRequest)
    {
        string name = "FritzTV.Test." + Guid.NewGuid().ToString("N");
        await using var primary = await SingleInstanceActivation.CreateOrActivateAsync(name, TestContext.Current.CancellationToken);
        Assert.NotNull(primary);
        using (var abandoned = new NamedPipeClientStream(".", primary.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await abandoned.ConnectAsync(1000, TestContext.Current.CancellationToken);
            if (sendRequest)
            {
                await abandoned.WriteAsync(new byte[] { 1 }, TestContext.Current.CancellationToken);
                await abandoned.FlushAsync(TestContext.Current.CancellationToken);
            }
        }

        primary.StartListening();
        Task<SingleInstanceActivation?>[] requests = Enumerable.Range(0, 8).Select(_ => SingleInstanceActivation.CreateOrActivateAsync(name, TestContext.Current.CancellationToken)).ToArray();
        var responses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.All(responses, Assert.Null);
    }

    [Fact]
    public async Task ACancelledSecondaryDoesNotReleaseThePrimaryOwnership()
    {
        string name = "FritzTV.Test." + Guid.NewGuid().ToString("N");
        await using var primary = await SingleInstanceActivation.CreateOrActivateAsync(name, TestContext.Current.CancellationToken);
        Assert.NotNull(primary);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<SingleInstanceActivation?> cancelled = SingleInstanceActivation.CreateOrActivateAsync(name, cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        primary.StartListening();
        Assert.Null(await SingleInstanceActivation.CreateOrActivateAsync(name, TestContext.Current.CancellationToken));
    }
}
