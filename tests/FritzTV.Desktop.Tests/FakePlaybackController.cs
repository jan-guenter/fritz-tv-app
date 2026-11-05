using FritzTV.Core.Models;
using FritzTV.Desktop.Services;

namespace FritzTV.Desktop.Tests;

internal sealed class FakePlaybackController : IPlaybackController
{
    public event EventHandler? TracksChanged
    {
        add { }
        remove { }
    }

    public event EventHandler? PlaybackFailed
    {
        add { }
        remove { }
    }

    public IReadOnlyList<PlaybackTrack> AudioTracks => [];

    public IReadOnlyList<PlaybackTrack> SubtitleTracks => [];

    public int? TeletextPage { get; private set; }

    public int Volume { get; private set; }

    public string? LastFilePath { get; private set; }

    public int? LastFileServiceId { get; private set; }

    public Exception? PlayFailure { get; init; }

    public Task PlayAsync(TvChannel channel, CancellationToken cancellationToken) => PlayFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task TogglePauseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PlayFileAsync(string path, CancellationToken cancellationToken, int? serviceId = null)
    {
        LastFilePath = path;
        LastFileServiceId = serviceId;
        return Task.CompletedTask;
    }

    public Task SeekAsync(DateTimeOffset? position, CancellationToken cancellationToken) => Task.CompletedTask;

    public ReceptionSnapshot Reception => new([], null, 0, 0);

    public PlaybackSnapshot? GetSnapshot() => null;

    public IReadOnlyList<Programme> Guide { get; init; } = [];

    public TvChannel? CurrentChannel { get; init; }

    public int? CurrentServiceId => null;

    public void SetVolume(int volume) => Volume = volume;

    public void SetAudioTrack(int trackId) { }

    public void SetSubtitleTrack(int trackId) { }

    public void SetTeletextPage(int page) => TeletextPage = page;

    public void NavigateTeletext(string key) { }

    public void CloseTeletext() => TeletextPage = 0;

    public Task ToggleTeletextTransparencyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
