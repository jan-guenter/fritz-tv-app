using FritzTV.Core.Models;
using FritzTV.Infrastructure.Persistence;
using FritzTV.Infrastructure.Playback;
using FritzTV.Infrastructure.Recording;

namespace FritzTV.Desktop.Services;

public sealed class RecordingActions(LibVlcPlaybackController player, RecordingCoordinator recordings, AppRepository repository)
{
    public async Task<RecordingInstance> StartAsync(TvChannel channel, int startMode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        AppSettings settings = await repository.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        BufferedPlayback? playback = player.BufferedSource;
        if (!string.Equals(playback?.Channel.Id, channel.Id, StringComparison.Ordinal))
        {
            playback = null;
        }

        DateTimeOffset? from = null;
        if (startMode != 0)
        {
            if (playback?.Buffer.Range is not { } range)
            {
                throw new InvalidOperationException("Select the currently playing channel to record retained content.");
            }

            from = range.Earliest;
            if (startMode == 1)
            {
                DateTimeOffset position = player.GetSnapshot()?.Position ?? range.Live;
                DvbServiceIdentity? identity = playback.Channel.ServiceIdentity;
                Programme? programme = playback.GuideSnapshot.FirstOrDefault(item =>
                    (identity is not null ? item.Service == identity : item.IsActualTransport && item.Service.ServiceId == playback.Programme?.ServiceId)
                    && item.Start <= position && item.End > position);
                from = programme is null || programme.Start < range.Earliest ? range.Earliest : programme.Start;
            }
        }

        return await recordings.StartInstantAsync(channel, settings, playback, from, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
