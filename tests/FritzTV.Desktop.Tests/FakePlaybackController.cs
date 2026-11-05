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

    public Task PlayAsync(TvChannel channel, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void TogglePause() { }

    public void SetVolume(int volume) => Volume = volume;

    public void SetAudioTrack(int trackId) { }

    public void SetSubtitleTrack(int trackId) { }

    public void SetTeletextPage(int page) => TeletextPage = page;

    public void NavigateTeletext(string key) { }

    public void ToggleTeletextTransparency() { }
}
