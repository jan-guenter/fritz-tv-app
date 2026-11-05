using FritzTV.Core.Models;

namespace FritzTV.Desktop.Services;

public interface IPlaybackController
{
    event EventHandler? TracksChanged;

    event EventHandler? PlaybackFailed;

    IReadOnlyList<PlaybackTrack> AudioTracks { get; }

    IReadOnlyList<PlaybackTrack> SubtitleTracks { get; }

    Task PlayAsync(TvChannel channel, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    Task PlayFileAsync(string path, CancellationToken cancellationToken);

    Task SeekAsync(DateTimeOffset? position, CancellationToken cancellationToken);

    Task TogglePauseAsync(CancellationToken cancellationToken);

    PlaybackSnapshot? GetSnapshot();

    IReadOnlyList<Programme> Guide { get; }

    TvChannel? CurrentChannel { get; }

    int? CurrentServiceId { get; }

    void SetVolume(int volume);

    void SetAudioTrack(int trackId);

    void SetSubtitleTrack(int trackId);

    void SetTeletextPage(int page);

    void NavigateTeletext(string key);

    void ToggleTeletextTransparency();
}
