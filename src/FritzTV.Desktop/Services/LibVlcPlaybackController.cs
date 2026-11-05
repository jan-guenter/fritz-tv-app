using FritzTV.Core.Models;
using FritzTV.Infrastructure.Persistence;
using FritzTV.Infrastructure.Playback;
using FritzTV.Infrastructure.Timeshift;
using FritzTV.Infrastructure.Transport;
using LibVLCSharp.Shared;

namespace FritzTV.Desktop.Services;

public sealed class LibVlcPlaybackController(AppRepository repository, SatIpSessionManager sessions) : IPlaybackController, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _media;
    private StreamMediaInput? _input;
    private PauseControlledReadStream? _controlledInput;
    private TimeshiftReadStream? _reader;
    private CancellationTokenSource? _readerLifetime;
    private BufferedPlayback? _bufferedPlayback;
    private TvChannel? _currentChannel;
    private DateTimeOffset _origin;
    private DateTimeOffset? _pausedAt;
    private long _elapsedMilliseconds;
    private long _lengthMilliseconds;
    private long _warmupMilliseconds;
    private int _requestedVolume = 75;
    private volatile bool _outputReady;
    private bool _paused;
    private bool _filePlayback;
    private volatile bool _switchingMedia;
    private int _disposed;

    public event EventHandler? NativePlayerCreated;

    public event EventHandler? NativeOutputChanged;

    public event EventHandler? TracksChanged;

    public event EventHandler? PlaybackFailed;

    public MediaPlayer? NativePlayer => _mediaPlayer;

    public bool IsOutputReady => _outputReady;

    public TvChannel? CurrentChannel => _currentChannel;

    public int? CurrentServiceId => _bufferedPlayback?.Programme?.ServiceId;

    internal BufferedPlayback? BufferedSource => _bufferedPlayback;

    public IReadOnlyList<Programme> Guide => _bufferedPlayback?.GuideSnapshot ?? [];

    public IReadOnlyList<PlaybackTrack> AudioTracks => _mediaPlayer?.AudioTrackDescription
        .Select(track => new PlaybackTrack(track.Id, track.Name)).ToArray() ?? [];

    public IReadOnlyList<PlaybackTrack> SubtitleTracks => _mediaPlayer?.SpuDescription
        .Select(track => new PlaybackTrack(track.Id, track.Name)).ToArray() ?? [];

    public async Task PlayAsync(TvChannel channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            await StopCoreAsync().ConfigureAwait(true);
            EnsureInitialized();
            IReadOnlyList<SatIpDevice> devices = await repository.GetDevicesAsync(cancellationToken).ConfigureAwait(true);
            SatIpDevice device = devices.FirstOrDefault(item => item.Id == channel.DeviceId)
                ?? throw new InvalidOperationException("The channel's saved device is unavailable. Refresh its channel list.");
            AppSettings settings = await repository.GetSettingsAsync(cancellationToken).ConfigureAwait(true);
            _bufferedPlayback = await BufferedPlayback.StartAsync(sessions, device, channel, settings, repository, cancellationToken).ConfigureAwait(true);
            _currentChannel = _bufferedPlayback.Channel;
            _filePlayback = false;
            OpenBufferedMedia(null);
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(true);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PlayFileAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            await StopCoreAsync().ConfigureAwait(true);
            EnsureInitialized();
            _filePlayback = true;
            _warmupMilliseconds = 0;
            _origin = DateTimeOffset.UnixEpoch;
            _media = new Media(_libVlc!, new Uri(Path.GetFullPath(path)));
            StartNativeMedia();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SeekAsync(DateTimeOffset? position, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (_filePlayback && _mediaPlayer is not null)
            {
                _mediaPlayer.Time = Math.Max(0, (long)((position ?? DateTimeOffset.UnixEpoch) - DateTimeOffset.UnixEpoch).TotalMilliseconds);
            }
            else if (_bufferedPlayback is not null)
            {
                bool remainPaused = _paused && position is not null;
                await StopDecoderAsync().ConfigureAwait(true);
                _paused = remainPaused;
                OpenBufferedMedia(position);
                _pausedAt = remainPaused ? _reader!.TargetStart : null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TogglePauseAsync(CancellationToken cancellationToken)
    {
        PlaybackSnapshot? snapshot = GetSnapshot();
        if (snapshot is null || _mediaPlayer is null)
        {
            return;
        }

        if (_paused && snapshot.ResumePointExpired)
        {
            _paused = false;
            _pausedAt = null;
            await SeekAsync(snapshot.Earliest, cancellationToken).ConfigureAwait(true);
        }
        else
        {
            _paused = !_paused;
            _pausedAt = _paused ? snapshot.Position : null;
            _controlledInput?.SetPaused(_paused);
            _mediaPlayer.SetPause(_paused);
        }
    }

    public PlaybackSnapshot? GetSnapshot()
    {
        if (_bufferedPlayback?.Buffer.Range is { } range)
        {
            DateTimeOffset cursor = _pausedAt ?? _origin.AddMilliseconds(Math.Max(0, Interlocked.Read(ref _elapsedMilliseconds)));
            bool expired = _pausedAt is { } paused && paused < range.Earliest;
            return new PlaybackSnapshot(true, _paused, range.Earliest, range.Live, range.Clamp(cursor), expired, _bufferedPlayback.Failure?.Message);
        }

        if (_filePlayback)
        {
            long length = Math.Max(0, Interlocked.Read(ref _lengthMilliseconds));
            long elapsed = Math.Clamp(Interlocked.Read(ref _elapsedMilliseconds), 0, length);
            return new PlaybackSnapshot(false, _paused, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMilliseconds(length), DateTimeOffset.UnixEpoch.AddMilliseconds(elapsed), false, null);
        }

        return null;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            await StopCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SetVolume(int volume)
    {
        _requestedVolume = Math.Clamp(volume, 0, 100);
        ApplyOutputState();
    }

    public void ApplyOutputState()
    {
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Volume = _outputReady ? _requestedVolume : 0;
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            await StopCoreAsync().ConfigureAwait(true);
            _mediaPlayer?.Dispose();
            _mediaPlayer = null;
            _libVlc?.Dispose();
            _libVlc = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void EnsureInitialized()
    {
        if (_mediaPlayer is not null)
        {
            return;
        }

        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show", "--avcodec-hw=any", "--deinterlace=-1", "--deinterlace-mode=yadif");
        _mediaPlayer = new MediaPlayer(_libVlc) { EnableKeyInput = false, EnableMouseInput = false };
        _mediaPlayer.ESAdded += (_, _) => TracksChanged?.Invoke(this, EventArgs.Empty);
        _mediaPlayer.ESDeleted += (_, _) => TracksChanged?.Invoke(this, EventArgs.Empty);
        _mediaPlayer.TimeChanged += (_, args) =>
        {
            Interlocked.Exchange(ref _elapsedMilliseconds, args.Time);
            if (!_outputReady && args.Time >= Interlocked.Read(ref _warmupMilliseconds))
            {
                _outputReady = true;
                NativeOutputChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        _mediaPlayer.LengthChanged += (_, args) => Interlocked.Exchange(ref _lengthMilliseconds, args.Length);
        _mediaPlayer.EncounteredError += (_, _) =>
        {
            if (!_switchingMedia)
            {
                PlaybackFailed?.Invoke(this, EventArgs.Empty);
            }
        };
        NativePlayerCreated?.Invoke(this, EventArgs.Empty);
    }

    private void OpenBufferedMedia(DateTimeOffset? position)
    {
        _readerLifetime = new CancellationTokenSource();
        _reader = _bufferedPlayback!.Buffer.OpenReader(position, _readerLifetime.Token);
        _origin = _reader.ActualStart;
        _warmupMilliseconds = (long)_reader.WarmupDuration.TotalMilliseconds;
        _controlledInput = new PauseControlledReadStream(_reader, _readerLifetime.Token);
        _controlledInput.SetPaused(_paused);
        _input = new StreamMediaInput(_controlledInput);
        _media = new Media(_libVlc!, _input);
        _media.AddOption(":demux=ts");
        _media.AddOption(":network-caching=1000");
        if (_paused)
        {
            _media.AddOption(":start-paused");
        }

        StartNativeMedia();
    }

    private void StartNativeMedia()
    {
        Interlocked.Exchange(ref _elapsedMilliseconds, 0);
        Interlocked.Exchange(ref _lengthMilliseconds, 0);
        _outputReady = _warmupMilliseconds == 0;
        ApplyOutputState();
        NativeOutputChanged?.Invoke(this, EventArgs.Empty);
        if (!_mediaPlayer!.Play(_media!))
        {
            throw new InvalidOperationException("LibVLC could not start playback.");
        }
    }

    private async Task StopDecoderAsync()
    {
        _switchingMedia = true;
        if (_readerLifetime is not null)
        {
            await _readerLifetime.CancelAsync().ConfigureAwait(true);
        }

        if (_mediaPlayer is not null)
        {
            await Task.Run(_mediaPlayer.Stop).ConfigureAwait(true);
        }

        _media?.Dispose();
        _media = null;
        _input?.Dispose();
        _input = null;
        _controlledInput?.Dispose();
        _controlledInput = null;
        _reader?.Dispose();
        _reader = null;
        _readerLifetime?.Dispose();
        _readerLifetime = null;
        _switchingMedia = false;
    }

    private async Task StopCoreAsync()
    {
        await StopDecoderAsync().ConfigureAwait(true);
        if (_bufferedPlayback is not null)
        {
            await _bufferedPlayback.DisposeAsync().ConfigureAwait(true);
            _bufferedPlayback = null;
        }

        _currentChannel = null;
        _filePlayback = false;
        _paused = false;
        _pausedAt = null;
    }
}
