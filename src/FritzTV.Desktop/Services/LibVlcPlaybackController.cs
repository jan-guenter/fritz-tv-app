using System.Globalization;
using System.Runtime.InteropServices;
using FritzTV.Core.Models;
using FritzTV.Infrastructure.Persistence;
using FritzTV.Infrastructure.Playback;
using FritzTV.Infrastructure.Timeshift;
using FritzTV.Infrastructure.Transport;
using LibVLCSharp.Shared;

namespace FritzTV.Desktop.Services;

public sealed class LibVlcPlaybackController(AppRepository repository, SatIpSessionManager sessions) : IPlaybackController, IAsyncDisposable
{
    private static int _linuxLoaderConfigured;
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
    private DateTimeOffset? _pendingBufferedSeek;
    private long _elapsedMilliseconds;
    private long _lengthMilliseconds;
    private long _warmupMilliseconds;
    private int _requestedVolume = 75;
    private int _teletextPage;
    private bool _teletextOpaque = true;
    private bool _restoreTeletext;
    private volatile bool _outputReady;
    private bool _paused;
    private bool _filePlayback;
    private string? _filePath;
    private int? _fileServiceId;
    private bool _pendingFileOpacityChange;
    private volatile TaskCompletionSource? _nativePlaying;
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

    public ReceptionSnapshot Reception => new(sessions.GetUsageSnapshot(), _bufferedPlayback?.ReceptionStatus, _bufferedPlayback?.RtpPackets ?? 0, _bufferedPlayback?.LostPackets ?? 0);

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
            await OpenBufferedMediaAsync(null).ConfigureAwait(true);
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

    public async Task PlayFileAsync(string path, CancellationToken cancellationToken, int? serviceId = null)
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
            _filePath = Path.GetFullPath(path);
            _fileServiceId = serviceId;
            _media = CreateFileMedia(_filePath);
            await StartNativeMediaAsync().ConfigureAwait(true);
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
            await SeekCoreAsync(position).ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SeekCoreAsync(DateTimeOffset? position)
    {
        if (_filePlayback && _mediaPlayer is not null)
        {
            _mediaPlayer.Time = Math.Max(0, (long)((position ?? DateTimeOffset.UnixEpoch) - DateTimeOffset.UnixEpoch).TotalMilliseconds);
        }
        else if (_bufferedPlayback?.Buffer.Range is { } range)
        {
            bool remainPaused = _paused && position is not null;
            await StopDecoderAsync().ConfigureAwait(true);
            _paused = remainPaused;
            _pausedAt = remainPaused ? range.Clamp(position!.Value) : null;
            _pendingBufferedSeek = _pausedAt;
            if (remainPaused)
            {
                // Keep capture running but defer decoder startup until resume. Starting a demuxer
                // with a closed input gate would race VLC's initial paused state against resume.
                _outputReady = false;
                ApplyOutputState();
                NativeOutputChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                await OpenBufferedMediaAsync(position).ConfigureAwait(true);
            }
        }
    }

    public async Task TogglePauseAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            PlaybackSnapshot? snapshot = GetSnapshot();
            if (snapshot is null || _mediaPlayer is null)
            {
                return;
            }

            if (_filePlayback && _paused && _pendingFileOpacityChange)
            {
                _paused = false;
                _pausedAt = null;
                await RestartFileMediaAsync(snapshot.Position, cancellationToken).ConfigureAwait(true);
            }
            else if (_paused && (snapshot.ResumePointExpired || _pendingBufferedSeek is not null))
            {
                DateTimeOffset target = snapshot.ResumePointExpired ? snapshot.Earliest : _pendingBufferedSeek!.Value;
                _paused = false;
                _pausedAt = null;
                _pendingBufferedSeek = null;
                await SeekCoreAsync(target).ConfigureAwait(true);
            }
            else
            {
                _paused = !_paused;
                _pausedAt = _paused ? snapshot.Position : null;
                _controlledInput?.SetPaused(_paused);
                _mediaPlayer.SetPause(_paused);
            }
        }
        finally
        {
            _gate.Release();
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
            return PlaybackSnapshot.ForRecording(_paused, Interlocked.Read(ref _elapsedMilliseconds), Interlocked.Read(ref _lengthMilliseconds));
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
            if (_outputReady && _restoreTeletext && _teletextPage != 0 && _mediaPlayer.State is VLCState.Playing or VLCState.Paused)
            {
                _restoreTeletext = false;
                _mediaPlayer.Teletext = _teletextPage;
            }
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
        _teletextPage = page;
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Teletext = page;
        }
    }

    public void NavigateTeletext(string key)
    {
        if (_mediaPlayer is not null && Enum.TryParse(key, ignoreCase: false, out TeletextKey navigation))
        {
            if (navigation == TeletextKey.Index)
            {
                // VLC 3 rejects navigation keys while teletext is closed; page100
                // explicitly enables the selected teletext stream as well.
                SetTeletextPage(100);
            }
            else
            {
                _mediaPlayer.Teletext = (int)navigation;
            }
        }
    }

    public void CloseTeletext()
    {
        _teletextPage = 0;
        _restoreTeletext = false;
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Teletext = 0;
        }
    }

    public async Task ToggleTeletextTransparencyAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (GetSnapshot() is not { } snapshot)
            {
                return;
            }

            // VLC 3's public ToggleTeletext switches the subtitle ES on/off. Its
            // supported vbi-opaque decoder option requires recreating the decoder.
            _teletextOpaque = !_teletextOpaque;
            _restoreTeletext = _teletextPage != 0;
            if (_filePlayback)
            {
                // Keep a paused file at its exact cursor; apply the decoder option on resume.
                _pendingFileOpacityChange = _paused;
                if (!_paused)
                {
                    await RestartFileMediaAsync(snapshot.Position, cancellationToken).ConfigureAwait(true);
                }
            }
            else
            {
                await SeekCoreAsync(snapshot.Position).ConfigureAwait(true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private Media CreateFileMedia(string path)
    {
        var media = new Media(_libVlc!, new Uri(path));
        media.AddOption(_teletextOpaque ? ":vbi-opaque" : ":no-vbi-opaque");
        if (_fileServiceId is { } serviceId && string.Equals(Path.GetExtension(path), ".ts", StringComparison.OrdinalIgnoreCase))
        {
            media.AddOption(":program=" + serviceId.ToString(CultureInfo.InvariantCulture));
        }

        return media;
    }

    private async Task RestartFileMediaAsync(DateTimeOffset position, CancellationToken cancellationToken)
    {
        long elapsed = Math.Max(0, (long)(position - DateTimeOffset.UnixEpoch).TotalMilliseconds);
        long length = Interlocked.Read(ref _lengthMilliseconds);
        await StopDecoderAsync().ConfigureAwait(true);
        _media = CreateFileMedia(_filePath!);
        _pendingFileOpacityChange = false;
        _restoreTeletext = _teletextPage != 0;
        _warmupMilliseconds = Math.Max(250, elapsed);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _nativePlaying = started;
        try
        {
            await StartNativeMediaAsync(elapsed, length).ConfigureAwait(true);
            // A file input must be active before VLC accepts a time seek. Applying
            // start-time alone can restart transport recordings at their beginning.
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(true);
            _mediaPlayer!.Time = elapsed;
            Interlocked.Exchange(ref _elapsedMilliseconds, elapsed);
        }
        finally
        {
            _nativePlaying = null;
        }
    }

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

        if (OperatingSystem.IsLinux() && Interlocked.Exchange(ref _linuxLoaderConfigured, 1) == 0)
        {
            NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, (name, _, _) =>
                string.Equals(name, "libvlc", StringComparison.Ordinal) ? NativeLibrary.Load("libvlc.so.5") : IntPtr.Zero);
        }

        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show", "--avcodec-hw=any", "--deinterlace=-1", "--deinterlace-mode=yadif");
        _mediaPlayer = new MediaPlayer(_libVlc) { EnableKeyInput = false, EnableMouseInput = false };
        _mediaPlayer.ESAdded += (_, _) => TracksChanged?.Invoke(this, EventArgs.Empty);
        _mediaPlayer.ESDeleted += (_, _) => TracksChanged?.Invoke(this, EventArgs.Empty);
        _mediaPlayer.Playing += (_, _) =>
        {
            _nativePlaying?.TrySetResult();
            NativeOutputChanged?.Invoke(this, EventArgs.Empty);
        };
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

    private async Task OpenBufferedMediaAsync(DateTimeOffset? position)
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
        _media.AddOption(":program=" + _bufferedPlayback.Programme!.ServiceId.ToString(CultureInfo.InvariantCulture));
        _media.AddOption(":network-caching=1000");
        _media.AddOption(_teletextOpaque ? ":vbi-opaque" : ":no-vbi-opaque");
        _restoreTeletext = _teletextPage != 0;
        await StartNativeMediaAsync().ConfigureAwait(true);
    }

    private async Task StartNativeMediaAsync(long initialPositionMilliseconds = 0, long initialLengthMilliseconds = 0)
    {
        Interlocked.Exchange(ref _elapsedMilliseconds, initialPositionMilliseconds);
        Interlocked.Exchange(ref _lengthMilliseconds, initialLengthMilliseconds);
        _outputReady = _warmupMilliseconds == 0;
        ApplyOutputState();
        NativeOutputChanged?.Invoke(this, EventArgs.Empty);
        if (!await Task.Run(() => _mediaPlayer!.Play(_media!)).ConfigureAwait(true))
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

        _teletextPage = 0;
        _restoreTeletext = false;
        _currentChannel = null;
        _filePlayback = false;
        _filePath = null;
        _fileServiceId = null;
        _pendingFileOpacityChange = false;
        _paused = false;
        _pausedAt = null;
        _pendingBufferedSeek = null;
        NativeOutputChanged?.Invoke(this, EventArgs.Empty);
    }
}
