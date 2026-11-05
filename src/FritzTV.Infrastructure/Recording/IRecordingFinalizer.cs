using FritzTV.Core.Models;

namespace FritzTV.Infrastructure.Recording;

public interface IRecordingFinalizer
{
    Task<RecordingFinalizationResult> FinalizeAsync(string sourcePath, string outputPath, TvChannel channel, bool retainSource, CancellationToken cancellationToken = default);

    Task ValidateAsync(string path, ChannelKind kind, CancellationToken cancellationToken = default);
}
