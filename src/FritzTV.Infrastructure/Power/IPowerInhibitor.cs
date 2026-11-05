namespace FritzTV.Infrastructure.Power;

public interface IPowerInhibitor
{
    ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default);
}
