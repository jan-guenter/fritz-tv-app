namespace FritzTV.Infrastructure.Recording;

public interface IMediaProcessRunner
{
    Task<MediaProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}
