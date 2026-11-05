using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FritzTV.Core.Models;
using FritzTV.Desktop.Localization;
using FritzTV.Desktop.Services;
using FritzTV.Infrastructure.Devices;
using FritzTV.Infrastructure.Persistence;

namespace FritzTV.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly SatIpDeviceClient _devices;
    private readonly SsdpDiscovery _discovery;
    private readonly IPlaybackController _playback;
    private readonly List<TvChannel> _allChannels = [];
    private readonly AppRepository? _repository;
    private AppSettings _settings = new();
    private bool _favoritesOnly;
    private bool _startAtLogin;
    private string _recordingDirectory = string.Empty;
    private string _bufferDirectory = string.Empty;
    private int _timeshiftMinutes = 60;
    private int _timeshiftGiB = 10;
    private string _descriptorUrl = string.Empty;
    private string _searchText = string.Empty;
    private string _statusText;
    private TvChannel? _selectedChannel;
    private Uri? _selectedDevice;
    private int _channelFilter;
    private int _volume = 75;
    private bool _german;
    private string _teletextPage = "100";
    private PlaybackTrack? _selectedAudioTrack;
    private PlaybackTrack? _selectedSubtitleTrack;

    public MainWindowViewModel(SatIpDeviceClient devices, SsdpDiscovery discovery, IPlaybackController playback, UiText text, AppRepository? repository = null, TimeProvider? timeProvider = null)
    {
        _devices = devices;
        _discovery = discovery;
        _playback = playback;
        Text = text;
        _repository = repository;
        _clock = timeProvider ?? TimeProvider.System;
        _german = text.IsGerman;
        _statusText = Text["Ready"];
        _playback.TracksChanged += OnPlaybackTracksChanged;
        _playback.PlaybackFailed += OnPlaybackFailed;
    }

    public UiText Text { get; }

    public ObservableCollection<TvChannel> Channels { get; } = [];

    public ObservableCollection<Uri> DiscoveredDevices { get; } = [];

    public ObservableCollection<PlaybackTrack> AudioTracks { get; } = [];

    public ObservableCollection<PlaybackTrack> SubtitleTracks { get; } = [];

    public IReadOnlyList<string> ChannelFilters => [Text["AllChannels"], Text["Television"], Text["Radio"]];

    public string DescriptorUrl
    {
        get => _descriptorUrl;
        set => SetProperty(ref _descriptorUrl, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                FilterChannels();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public TvChannel? SelectedChannel
    {
        get => _selectedChannel;
        set => SetProperty(ref _selectedChannel, value);
    }

    public Uri? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value) && value is not null)
            {
                DescriptorUrl = value.AbsoluteUri;
            }
        }
    }

    public int ChannelFilter
    {
        get => _channelFilter;
        set
        {
            if (SetProperty(ref _channelFilter, value))
            {
                FilterChannels();
            }
        }
    }

    public int Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, Math.Clamp(value, 0, 100)))
            {
                _playback.SetVolume(_volume);
            }
        }
    }

    public bool German
    {
        get => _german;
        set
        {
            if (SetProperty(ref _german, value))
            {
                Text.SetLanguage(value);
                OnPropertyChanged(nameof(ChannelFilters));
                OnPropertyChanged(nameof(PauseButtonText));
                StatusText = Text["Ready"];
            }
        }
    }

    public string TeletextPage
    {
        get => _teletextPage;
        set => SetProperty(ref _teletextPage, value);
    }

    public PlaybackTrack? SelectedAudioTrack
    {
        get => _selectedAudioTrack;
        set
        {
            if (SetProperty(ref _selectedAudioTrack, value) && value is not null)
            {
                _playback.SetAudioTrack(value.Id);
            }
        }
    }

    public PlaybackTrack? SelectedSubtitleTrack
    {
        get => _selectedSubtitleTrack;
        set
        {
            if (SetProperty(ref _selectedSubtitleTrack, value) && value is not null)
            {
                _playback.SetSubtitleTrack(value.Id);
            }
        }
    }

    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set
        {
            if (SetProperty(ref _favoritesOnly, value))
            {
                FilterChannels();
            }
        }
    }

    public bool StartAtLogin
    {
        get => _startAtLogin;
        set => SetProperty(ref _startAtLogin, value);
    }

    public string RecordingDirectory
    {
        get => _recordingDirectory;
        set => SetProperty(ref _recordingDirectory, value);
    }

    public string BufferDirectory
    {
        get => _bufferDirectory;
        set => SetProperty(ref _bufferDirectory, value);
    }

    public int TimeshiftMinutes
    {
        get => _timeshiftMinutes;
        set => SetProperty(ref _timeshiftMinutes, value);
    }

    public int TimeshiftGiB
    {
        get => _timeshiftGiB;
        set => SetProperty(ref _timeshiftGiB, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_repository is null)
        {
            return;
        }

        await _repository.InitializeAsync(cancellationToken).ConfigureAwait(true);
        _settings = await _repository.GetSettingsAsync(cancellationToken).ConfigureAwait(true);
        if (!string.Equals(_settings.Language, "system", StringComparison.Ordinal))
        {
            German = string.Equals(_settings.Language, "de", StringComparison.Ordinal);
        }

        StartAtLogin = _settings.StartAtLogin;
        RecordingDirectory = _settings.RecordingDirectory;
        BufferDirectory = _settings.BufferDirectory;
        TimeshiftMinutes = (int)_settings.TimeshiftDuration.TotalMinutes;
        TimeshiftGiB = (int)(_settings.TimeshiftBytes / (1024L * 1024 * 1024));
        IReadOnlyList<SatIpDevice> storedDevices = await _repository.GetDevicesAsync(cancellationToken).ConfigureAwait(true);
        foreach (SatIpDevice device in storedDevices)
        {
            DiscoveredDevices.Add(device.DescriptorUri);
        }

        SelectedDevice = storedDevices.Count > 0 ? storedDevices[0].DescriptorUri : null;
        _allChannels.AddRange(await _repository.GetChannelsAsync(cancellationToken).ConfigureAwait(true));
        FilterChannels();
    }

    public void ReportStartupError(string message) => StatusText = Text["InitializationFailed"] + " " + message;

    [RelayCommand]
    private async Task ToggleFavoriteAsync(CancellationToken cancellationToken)
    {
        if (SelectedChannel is null)
        {
            return;
        }

        TvChannel updated = SelectedChannel with { IsFavorite = !SelectedChannel.IsFavorite };
        int index = _allChannels.FindIndex(channel => string.Equals(channel.Id, updated.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            _allChannels[index] = updated;
        }

        if (_repository is not null)
        {
            await _repository.SetFavoriteAsync(updated.Id, updated.IsFavorite, cancellationToken).ConfigureAwait(true);
        }

        FilterChannels();
        SelectedChannel = updated;
    }

    [RelayCommand]
    private async Task SaveSettingsAsync(CancellationToken cancellationToken)
    {
        if (TimeshiftMinutes < 1 || TimeshiftGiB < 1 || !Path.IsPathFullyQualified(RecordingDirectory) || !Path.IsPathFullyQualified(BufferDirectory))
        {
            StatusText = Text["InvalidSettings"];
            return;
        }

        try
        {
            Directory.CreateDirectory(RecordingDirectory);
            Directory.CreateDirectory(BufferDirectory);
            await LoginAutostart.ApplyAsync(StartAtLogin, cancellationToken).ConfigureAwait(true);
            _settings = _settings with
            {
                Language = German ? "de" : "en",
                StartAtLogin = StartAtLogin,
                RecordingDirectory = RecordingDirectory,
                BufferDirectory = BufferDirectory,
                TimeshiftDuration = TimeSpan.FromMinutes(TimeshiftMinutes),
                TimeshiftBytes = checked(TimeshiftGiB * 1024L * 1024 * 1024),
            };
            if (_repository is not null)
            {
                await _repository.SaveSettingsAsync(_settings, cancellationToken).ConfigureAwait(true);
            }

            StatusText = Text["SettingsSaved"];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusText = Text["SettingsFailed"] + " " + exception.Message;
        }
    }

    [RelayCommand]
    private async Task ImportAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(DescriptorUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            StatusText = Text["InvalidDescriptor"];
            return;
        }

        try
        {
            StatusText = Text["Loading"];
            SatIpDevice device = await _devices.GetDeviceAsync(uri, cancellationToken).ConfigureAwait(true);
            IReadOnlyList<TvChannel> imported = await _devices.GetChannelsAsync(device, cancellationToken).ConfigureAwait(true);
            var favorites = _allChannels.Where(channel => channel.IsFavorite).Select(channel => channel.Id).ToHashSet(StringComparer.Ordinal);
            TvChannel[] updated = imported.Select(channel => channel with { IsFavorite = favorites.Contains(channel.Id) }).ToArray();
            _allChannels.RemoveAll(channel => channel.DeviceId == device.Id);
            _allChannels.AddRange(updated);
            if (_repository is not null)
            {
                await _repository.SaveDeviceAsync(device, cancellationToken).ConfigureAwait(true);
                await _repository.ReplaceDeviceChannelsAsync(device.Id, updated, cancellationToken).ConfigureAwait(true);
                _allChannels.Clear();
                _allChannels.AddRange(await _repository.GetChannelsAsync(cancellationToken).ConfigureAwait(true));
            }
            FilterChannels();
            StatusText = string.Format(CultureInfo.CurrentCulture, Text["Imported"], device.Name, imported.Count, device.TunerCount);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusText = Text["ImportFailed"] + " " + exception.Message;
        }
    }

    [RelayCommand]
    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            StatusText = Text["Discovering"];
            IReadOnlyList<Uri> devices = await _discovery.DiscoverAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(true);
            DiscoveredDevices.Clear();
            foreach (Uri uri in devices)
            {
                DiscoveredDevices.Add(uri);
            }

            SelectedDevice = devices.Count > 0 ? devices[0] : null;
            StatusText = string.Format(CultureInfo.CurrentCulture, Text["Discovered"], devices.Count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusText = Text["DiscoveryFailed"] + " " + exception.Message;
        }
    }

    [RelayCommand]
    private async Task PlayAsync(CancellationToken cancellationToken)
    {
        TvChannel? requested = SelectedChannel;
        if (requested is null)
        {
            StatusText = Text["SelectChannel"];
            return;
        }

        try
        {
            await _playback.PlayAsync(requested, cancellationToken).ConfigureAwait(true);
            _playback.SetVolume(Volume);
            TvChannel canonical = _playback.CurrentChannel ?? requested;
            if (!string.Equals(canonical.Id, requested.Id, StringComparison.Ordinal))
            {
                string? selection = SelectedChannel?.Id;
                int index = _allChannels.FindIndex(channel => string.Equals(channel.Id, requested.Id, StringComparison.Ordinal));
                if (index >= 0)
                {
                    _allChannels[index] = canonical;
                }

                FilterChannels();
                SelectedChannel = string.Equals(selection, requested.Id, StringComparison.Ordinal) ? canonical
                    : _allChannels.FirstOrDefault(channel => string.Equals(channel.Id, selection, StringComparison.Ordinal));
            }

            StatusText = string.Format(CultureInfo.CurrentCulture, Text["Playing"], canonical.Name);
            await RefreshPlaybackAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusText = Text["PlaybackFailed"] + " " + exception.Message;
        }
    }

    [RelayCommand]
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        PlayCommand.Cancel();
        SeekCommand.Cancel();
        await _playback.StopAsync(cancellationToken).ConfigureAwait(true);
        StatusText = Text["Ready"];
    }

    [RelayCommand]
    private async Task PauseAsync(CancellationToken cancellationToken) => await _playback.TogglePauseAsync(cancellationToken).ConfigureAwait(true);

    [RelayCommand]
    private void ShowTeletext()
    {
        if (int.TryParse(TeletextPage, NumberStyles.None, CultureInfo.InvariantCulture, out int page) && page is >= 100 and <= 899)
        {
            _playback.SetTeletextPage(page);
        }
        else
        {
            StatusText = Text["InvalidTeletext"];
        }
    }

    [RelayCommand]
    private void NavigateTeletext(string key) => _playback.NavigateTeletext(key);

    [RelayCommand]
    private void ToggleTeletextTransparency() => _playback.ToggleTeletextTransparency();

    private void FilterChannels()
    {
        Channels.Clear();
        foreach (TvChannel channel in _allChannels.Where(channel =>
            channel.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) && (!FavoritesOnly || channel.IsFavorite) &&
            (ChannelFilter == 0 || ChannelFilter == 1 && channel.Kind == ChannelKind.Television || ChannelFilter == 2 && channel.Kind == ChannelKind.Radio)))
        {
            Channels.Add(channel);
        }
    }

    private void RefreshTracks()
    {
        AudioTracks.Clear();
        foreach (PlaybackTrack track in _playback.AudioTracks)
        {
            AudioTracks.Add(track);
        }

        SubtitleTracks.Clear();
        foreach (PlaybackTrack track in _playback.SubtitleTracks)
        {
            SubtitleTracks.Add(track);
        }
    }
}
