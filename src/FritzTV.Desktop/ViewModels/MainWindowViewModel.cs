using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FritzTV.Core.Models;
using FritzTV.Desktop.Localization;
using FritzTV.Desktop.Services;
using FritzTV.Infrastructure.Devices;
using FritzTV.Infrastructure.Guide;
using FritzTV.Infrastructure.Recording;

namespace FritzTV.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly SatIpDeviceClient _devices;
    private readonly SsdpDiscovery _discovery;
    private readonly IPlaybackController _playback;
    private readonly List<TvChannel> _allChannels = [];
    private readonly List<SatIpDevice> _knownDevices = [];
    private DiscoveryInterface? _selectedDiscoveryInterface;
    private int _defaultStartPadding = 2;
    private int _defaultEndPadding = 5;
    private int _freeSpaceReserveGiB = 1;
    private int _guideRefreshHours = 6;
    private readonly IDesktopData? _data;
    private AppSettings _settings = new();
    private bool _favoritesOnly;
    private bool _startAtLogin;
    private bool _retainTransportStreams;
    private int _rtpFirstPort = 50000;
    private int _rtpLastPort = 50127;
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
    private bool _muted;
    private bool _german;
    private string _teletextPage = "100";
    private PlaybackTrack? _selectedAudioTrack;
    private PlaybackTrack? _selectedSubtitleTrack;

    public MainWindowViewModel(SatIpDeviceClient devices, SsdpDiscovery discovery, IPlaybackController playback, UiText text, IDesktopData? data = null, TimeProvider? timeProvider = null, GuideService? guideService = null, RecordingCoordinator? recordings = null, RecordingActions? recordingActions = null)
    {
        _devices = devices;
        _discovery = discovery;
        _playback = playback;
        Text = text;
        _data = data;
        _clock = timeProvider ?? TimeProvider.System;
        _guideDay = _clock.GetLocalNow();
        _guideService = guideService;
        _recordings = recordings;
        _recordingActions = recordingActions;
        InitializeRecordingEditor();
        _german = text.IsGerman;
        _statusText = Text["Ready"];
        DiscoveryInterfaces = NetworkDiscovery.GetInterfaces(Text["DefaultInterface"]);
        SelectedDiscoveryInterface = DiscoveryInterfaces[0];
        _playback.TracksChanged += OnPlaybackTracksChanged;
        _playback.PlaybackFailed += OnPlaybackFailed;
    }

    public UiText Text { get; }

    public ObservableCollection<TvChannel> Channels { get; } = [];

    public ObservableCollection<Uri> DiscoveredDevices { get; } = [];

    public IReadOnlyList<DiscoveryInterface> DiscoveryInterfaces { get; }

    public DiscoveryInterface? SelectedDiscoveryInterface
    {
        get => _selectedDiscoveryInterface;
        set => SetProperty(ref _selectedDiscoveryInterface, value);
    }

    public int DefaultStartPadding
    {
        get => _defaultStartPadding;
        set => SetProperty(ref _defaultStartPadding, value);
    }

    public int DefaultEndPadding
    {
        get => _defaultEndPadding;
        set => SetProperty(ref _defaultEndPadding, value);
    }

    public int GuideRefreshHours
    {
        get => _guideRefreshHours;
        set => SetProperty(ref _guideRefreshHours, value);
    }

    public int FreeSpaceReserveGiB
    {
        get => _freeSpaceReserveGiB;
        set => SetProperty(ref _freeSpaceReserveGiB, value);
    }

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
        set
        {
            if (SetProperty(ref _selectedChannel, value))
            {
                if (_timerProgramme is null || (value ?? _playback.CurrentChannel)?.ServiceIdentity != _timerProgramme.Service)
                {
                    ClearProgrammeLink();
                }

                FilterGuide();
                if (_data is not null)
                {
                    LoadChannelGuideCommand.Execute(null);
                }
            }
        }
    }

    public Uri? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                if (value is not null)
                {
                    DescriptorUrl = value.AbsoluteUri;
                }

                FilterChannels();
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
                _playback.SetVolume(Muted ? 0 : _volume);
            }
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (SetProperty(ref _muted, value))
            {
                _playback.SetVolume(value ? 0 : Volume);
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
                OnPropertyChanged(nameof(InstantStartModes));
                OnPropertyChanged(nameof(FullscreenButtonText));
                FilterGuide();
                RefreshRecordingsCommand.Execute(null);
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

    public bool RetainTransportStreams
    {
        get => _retainTransportStreams;
        set => SetProperty(ref _retainTransportStreams, value);
    }

    public int RtpFirstPort
    {
        get => _rtpFirstPort;
        set => SetProperty(ref _rtpFirstPort, value);
    }

    public int RtpLastPort
    {
        get => _rtpLastPort;
        set => SetProperty(ref _rtpLastPort, value);
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
        if (_data is null)
        {
            return;
        }

        await _data.InitializeAsync(cancellationToken).ConfigureAwait(true);
        _settings = await _data.GetSettingsAsync(cancellationToken).ConfigureAwait(true);
        if (!string.Equals(_settings.Language, "system", StringComparison.Ordinal))
        {
            German = string.Equals(_settings.Language, "de", StringComparison.Ordinal);
        }

        StartAtLogin = _settings.StartAtLogin;
        RetainTransportStreams = _settings.RetainTransportStreams;
        RtpFirstPort = _settings.RtpFirstPort;
        RtpLastPort = _settings.RtpLastPort;
        TimerRetainSource = _settings.RetainTransportStreams;
        DefaultStartPadding = TimerStartPadding = (int)_settings.DefaultStartPadding.TotalMinutes;
        DefaultEndPadding = TimerEndPadding = (int)_settings.DefaultEndPadding.TotalMinutes;
        FreeSpaceReserveGiB = (int)(_settings.FreeSpaceReserveBytes / (1024L * 1024 * 1024));
        GuideRefreshHours = (int)_settings.GuideRefreshInterval.TotalHours;
        RecordingDirectory = _settings.RecordingDirectory;
        BufferDirectory = _settings.BufferDirectory;
        TimeshiftMinutes = (int)_settings.TimeshiftDuration.TotalMinutes;
        TimeshiftGiB = (int)(_settings.TimeshiftBytes / (1024L * 1024 * 1024));
        IReadOnlyList<SatIpDevice> storedDevices = await _data.GetDevicesAsync(cancellationToken).ConfigureAwait(true);
        _knownDevices.AddRange(storedDevices);
        foreach (SatIpDevice device in storedDevices)
        {
            DiscoveredDevices.Add(device.DescriptorUri);
        }

        SelectedDevice = storedDevices.Count > 0 ? storedDevices[0].DescriptorUri : null;
        _allChannels.AddRange(await _data.GetChannelsAsync(cancellationToken).ConfigureAwait(true));
        FilterChannels();
        await RefreshRecordingsAsync(cancellationToken).ConfigureAwait(true);
        await RefreshGuideCoreAsync(cancellationToken).ConfigureAwait(true);
    }

    public void ReportStartupError(string message) => StatusText = Text["InitializationFailed"] + " " + message;

    [RelayCommand]
    private async Task ToggleFavoriteAsync(CancellationToken cancellationToken)
    {
        TvChannel? selected = SelectedChannel;
        if (selected is null)
        {
            return;
        }

        try
        {
            TvChannel updated = selected with { IsFavorite = !selected.IsFavorite };
            if (_data is not null)
            {
                await _data.SetFavoriteAsync(updated.Id, updated.IsFavorite, cancellationToken).ConfigureAwait(true);
                updated = await _data.GetChannelAsync(updated.Id, cancellationToken).ConfigureAwait(true) ?? updated;
            }

            string? selection = SelectedChannel?.Id;
            int index = _allChannels.FindIndex(channel => string.Equals(channel.Id, selected.Id, StringComparison.Ordinal) || string.Equals(channel.Id, updated.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                _allChannels[index] = updated;
            }

            FilterChannels();
            SelectedChannel = Channels.FirstOrDefault(channel => string.Equals(channel.Id, string.Equals(selection, selected.Id, StringComparison.Ordinal) ? updated.Id : selection, StringComparison.Ordinal));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText = Text["FavoriteFailed"] + " " + exception.Message;
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync(CancellationToken cancellationToken)
    {
        if (TimeshiftMinutes < 1 || TimeshiftGiB < 1 || DefaultStartPadding < 0 || DefaultEndPadding < 0 || FreeSpaceReserveGiB < 0 || GuideRefreshHours is < 1 or > 744 || RtpFirstPort < 1024 || RtpLastPort > 65535 || RtpFirstPort % 2 != 0 || RtpLastPort <= RtpFirstPort || !Path.IsPathFullyQualified(RecordingDirectory) || !Path.IsPathFullyQualified(BufferDirectory))
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
                RetainTransportStreams = RetainTransportStreams,
                RtpFirstPort = RtpFirstPort,
                RtpLastPort = RtpLastPort,
                RecordingDirectory = RecordingDirectory,
                BufferDirectory = BufferDirectory,
                TimeshiftDuration = TimeSpan.FromMinutes(TimeshiftMinutes),
                TimeshiftBytes = checked(TimeshiftGiB * 1024L * 1024 * 1024),
                FreeSpaceReserveBytes = checked(FreeSpaceReserveGiB * 1024L * 1024 * 1024),
                GuideRefreshInterval = TimeSpan.FromHours(GuideRefreshHours),
                DefaultStartPadding = TimeSpan.FromMinutes(DefaultStartPadding),
                DefaultEndPadding = TimeSpan.FromMinutes(DefaultEndPadding),
            };
            if (_data is not null)
            {
                await _data.SaveSettingsAsync(_settings, cancellationToken).ConfigureAwait(true);
            }

            StatusText = Text["SettingsSaved"];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = Text["Ready"];
        }
        catch (Exception exception)
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
            if (_data is not null)
            {
                await _data.SaveDeviceAsync(device, cancellationToken).ConfigureAwait(true);
                await _data.ReplaceDeviceChannelsAsync(device.Id, updated, cancellationToken).ConfigureAwait(true);
                IReadOnlyList<TvChannel> channels = await _data.GetChannelsAsync(cancellationToken).ConfigureAwait(true);
                _allChannels.Clear();
                _allChannels.AddRange(channels);
            }
            _knownDevices.RemoveAll(item => item.Id == device.Id);
            _knownDevices.Add(device);
            if (!DiscoveredDevices.Contains(device.DescriptorUri))
            {
                DiscoveredDevices.Add(device.DescriptorUri);
            }

            SelectedDevice = device.DescriptorUri;
            _guideService?.RequestRefresh();
            FilterChannels();
            StatusText = string.Format(CultureInfo.CurrentCulture, Text["Imported"], device.Name, imported.Count, device.TunerCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = Text["Ready"];
        }
        catch (Exception exception)
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
            IReadOnlyList<Uri> devices = SelectedDiscoveryInterface?.Address is null
                ? await _discovery.DiscoverAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(true)
                : await NetworkDiscovery.DiscoverAsync(SelectedDiscoveryInterface, cancellationToken).ConfigureAwait(true);
            DiscoveredDevices.Clear();
            foreach (Uri uri in _knownDevices.Select(device => device.DescriptorUri).Concat(devices).Distinct())
            {
                DiscoveredDevices.Add(uri);
            }

            SelectedDevice = devices.Count > 0 ? devices[0] : null;
            StatusText = string.Format(CultureInfo.CurrentCulture, Text["Discovered"], devices.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = Text["Ready"];
        }
        catch (Exception exception)
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
            DetailsExpanded = false;
            await _playback.PlayAsync(requested, cancellationToken).ConfigureAwait(true);
            _playback.SetVolume(Muted ? 0 : Volume);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = Text["Ready"];
        }
        catch (Exception exception)
        {
            StatusText = Text["PlaybackFailed"] + " " + exception.Message;
        }
    }

    [RelayCommand]
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        PlayCommand.Cancel();
        SeekCommand.Cancel();
        await RunPlaybackActionAsync(() => _playback.StopAsync(cancellationToken), cancellationToken).ConfigureAwait(true);
        StatusText = Text["Ready"];
    }

    [RelayCommand]
    private async Task PauseAsync(CancellationToken cancellationToken) => await RunPlaybackActionAsync(() => _playback.TogglePauseAsync(cancellationToken), cancellationToken).ConfigureAwait(true);

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
    private void CloseTeletext() => _playback.CloseTeletext();

    [RelayCommand]
    private async Task ToggleTeletextTransparencyAsync(CancellationToken cancellationToken) =>
        await RunPlaybackActionAsync(() => _playback.ToggleTeletextTransparencyAsync(cancellationToken), cancellationToken).ConfigureAwait(true);

    [RelayCommand]
    private void ClearDeviceFilter() => SelectedDevice = null;

    private void FilterChannels()
    {
        Guid? deviceId = _knownDevices.FirstOrDefault(device => device.DescriptorUri == SelectedDevice)?.Id;
        Channels.Clear();
        foreach (TvChannel channel in _allChannels.Where(channel =>
            (deviceId is null || channel.DeviceId == deviceId) && channel.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) && (!FavoritesOnly || channel.IsFavorite) &&
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
