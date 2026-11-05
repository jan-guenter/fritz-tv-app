using System.Globalization;
using FritzTV.Core.Models;
using LibVLCSharp.Shared;

namespace FritzTV.Desktop.Services;

public sealed class LibVlcPlaybackController : IPlaybackController, IDisposable
{
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _media;
    private bool _disposed;

    public event EventHandler? NativePlayerCreated;

    public event EventHandler? TracksChanged;

    public event EventHandler? PlaybackFailed;

    public MediaPlayer? NativePlayer => _mediaPlayer;

    public IReadOnlyList<PlaybackTrack> AudioTracks => _mediaPlayer?.AudioTrackDescription
        .Select(track => new PlaybackTrack(track.Id, track.Name)).ToArray() ?? [];

    public IReadOnlyList<PlaybackTrack> SubtitleTracks => _mediaPlayer?.SpuDescription
        .Select(track => new PlaybackTrack(track.Id, track.Name)).ToArray() ?? [];

    public async Task PlayAsync(TvChannel channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await StopAsync(cancellationToken).ConfigureAwait(true);
        EnsureInitialized();
        _media?.Dispose();
        _media = new Media(_libVlc!, channel.StreamUri);
        _media.AddOption(":network-caching=1000");
        _media.AddOption(":no-rtsp-tcp");
        string? port = Environment.GetEnvironmentVariable("FRITZTV_RTP_PORT");
        if (int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int rtpPort) && rtpPort is >= 1024 and <= 65534)
        {
            _media.AddOption(":rtp-client-port=" + rtpPort.ToString(CultureInfo.InvariantCulture));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!_mediaPlayer!.Play(_media))
        {
            throw new InvalidOperationException("LibVLC could not start playback.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_mediaPlayer is not null)
        {
            await Task.Run(_mediaPlayer.Stop, cancellationToken).ConfigureAwait(false);
        }
    }

    public void TogglePause() => _mediaPlayer?.Pause();

    public void SetVolume(int volume)
    {
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Volume = Math.Clamp(volume, 0, 100);
        }
    }

    public void SetAudioTrack(int trackId)
    {
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.SetAudioTrack(trackId);
        }
    }

    public void SetSubtitleTrack(int trackId)
    {
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.SetSpu(trackId);
        }
    }

    public void SetTeletextPage(int page)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, 899);
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Teletext = page;
        }
    }

    public void NavigateTeletext(string key)
    {
        if (_mediaPlayer is not null && Enum.TryParse(key, ignoreCase: false, out TeletextKey navigation))
        {
            _mediaPlayer.Teletext = (int)navigation;
        }
    }

    public void ToggleTeletextTransparency() => _mediaPlayer?.ToggleTeletext();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mediaPlayer?.Dispose();
        _media?.Dispose();
        _libVlc?.Dispose();
    }

    private void EnsureInitialized()
    {
        if (_mediaPlayer is not null)
        {
            return;
        }

        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show", "--avcodec-hw=any");
        _mediaPlayer = new MediaPlayer(_libVlc)
        {
            EnableKeyInput = false,
            EnableMouseInput = false,
        };
        _mediaPlayer.ESAdded += (_, _) => TracksChanged?.Invoke(this, EventArgs.Empty);
        _mediaPlayer.ESDeleted += (_, _) => TracksChanged?.Invoke(this, EventArgs.Empty);
        _mediaPlayer.EncounteredError += (_, _) => PlaybackFailed?.Invoke(this, EventArgs.Empty);
        NativePlayerCreated?.Invoke(this, EventArgs.Empty);
    }
}
