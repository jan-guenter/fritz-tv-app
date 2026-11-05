using FritzTV.Core.Models;
using FritzTV.Infrastructure.Playback;

namespace FritzTV.Infrastructure.Recording;

public interface IRecordingInputFactory
{
    Task<IRecordingInput> OpenAsync(TvChannel channel, BufferedPlayback? playback = null, DateTimeOffset? from = null, CancellationToken cancellationToken = default);
}
