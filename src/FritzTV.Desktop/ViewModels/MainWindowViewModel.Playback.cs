using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using FritzTV.Core.Models;
using FritzTV.Core.Scheduling;
using FritzTV.Desktop.Services;
using FritzTV.Infrastructure.Guide;

namespace FritzTV.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly TimeProvider _clock;
    private readonly GuideService? _guideService;
    private string _guideStatusText = string.Empty;
    private string _receptionSummary = string.Empty;
    private readonly CancellationTokenSource _viewModelLifetime = new();
    private DispatcherTimer? _updateTimer;
    private bool _refreshing;
    private bool _timelineDragging;
    private bool _paused;
    private bool _hasPlayback;
    private bool _isLivePlayback;
    private bool _expiredNotified;
    private double _timelinePosition;
    private double _timelineMaximum = 1;
    private string _timelineLabel = string.Empty;
    private int _guideTicks;
    private string _guideSearch = string.Empty;
    private ProgrammeItem? _selectedProgramme;
    private IReadOnlyList<Programme> _guideEntries = [];
    private int _disposed;
    private int _guideRequest;
    private DateTimeOffset? _guideDay;
    private double _guideTimelineWidth = 2880;
    private string _nowNextText = string.Empty;

    public ObservableCollection<ProgrammeItem> Programmes { get; } = [];

    public ObservableCollection<ProgrammeItem> TimelineProgrammes { get; } = [];

    public ObservableCollection<string> GuideHours { get; } = [];

    public DateTimeOffset? GuideDay
    {
        get => _guideDay;
        set
        {
            if (SetProperty(ref _guideDay, value))
            {
                FilterGuide();
            }
        }
    }

    public double GuideTimelineWidth
    {
        get => _guideTimelineWidth;
        private set => SetProperty(ref _guideTimelineWidth, value);
    }

    public double GuideNowOffset => Math.Clamp((_clock.GetUtcNow() - ScheduleCalculator.ResolveLocal((GuideDay ?? _clock.GetLocalNow()).Date, _clock.LocalTimeZone)).TotalMinutes * 2 - 120, 0, GuideTimelineWidth);

    public void ShowGuideToday() => GuideDay = _clock.GetLocalNow();

    public string NowNextText
    {
        get => _nowNextText;
        private set => SetProperty(ref _nowNextText, value);
    }

    public string ReceptionSummary
    {
        get => _receptionSummary;
        private set => SetProperty(ref _receptionSummary, value);
    }

    public string GuideStatusText
    {
        get => _guideStatusText;
        private set => SetProperty(ref _guideStatusText, value);
    }

    public string PauseButtonText => Text[_paused ? "Resume" : "Pause"];

    public bool HasPlayback
    {
        get => _hasPlayback;
        private set
        {
            if (SetProperty(ref _hasPlayback, value))
            {
                OnPropertyChanged(nameof(ShowTimeline));
                OnPropertyChanged(nameof(CanToggleFullscreen));
            }
        }
    }

    public bool IsLivePlayback
    {
        get => _isLivePlayback;
        private set => SetProperty(ref _isLivePlayback, value);
    }

    public double TimelinePosition
    {
        get => _timelinePosition;
        set => SetProperty(ref _timelinePosition, value);
    }

    public double TimelineMaximum
    {
        get => _timelineMaximum;
        private set => SetProperty(ref _timelineMaximum, value);
    }

    public string TimelineLabel
    {
        get => _timelineLabel;
        private set => SetProperty(ref _timelineLabel, value);
    }

    public string GuideSearch
    {
        get => _guideSearch;
        set
        {
            if (SetProperty(ref _guideSearch, value))
            {
                FilterGuide();
            }
        }
    }

    public ProgrammeItem? SelectedProgramme
    {
        get => _selectedProgramme;
        set => SetProperty(ref _selectedProgramme, value);
    }

    public void StartUpdates()
    {
        _updateTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _updateTimer.Tick += OnUpdateTick;
        _updateTimer.Start();
    }

    public void BeginTimelineDrag() => _timelineDragging = true;

    public void EndTimelineDrag() => _timelineDragging = false;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _updateTimer?.Stop();
        if (_updateTimer is not null)
        {
            _updateTimer.Tick -= OnUpdateTick;
        }

        PlayCommand.Cancel();
        ImportCommand.Cancel();
        ToggleFavoriteCommand.Cancel();
        DiscoverCommand.Cancel();
        SeekCommand.Cancel();
        LoadChannelGuideCommand.Cancel();
        RefreshGuideCommand.Cancel();
        _viewModelLifetime.Cancel();
        _viewModelLifetime.Dispose();
        if (_recordings is not null)
        {
            _recordings.Changed -= OnRecordingsChanged;
        }

        _playback.TracksChanged -= OnPlaybackTracksChanged;
        _playback.PlaybackFailed -= OnPlaybackFailed;
    }

    [RelayCommand]
    private async Task SeekAsync(CancellationToken cancellationToken)
    {
        PlaybackSnapshot? snapshot = _playback.GetSnapshot();
        if (snapshot is not null)
        {
            try
            {
                await _playback.SeekAsync(snapshot.Earliest.AddSeconds(TimelinePosition), cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                StatusText = Text["PlaybackFailed"] + " " + exception.Message;
            }
        }

        EndTimelineDrag();
    }

    [RelayCommand]
    private async Task RewindAsync(CancellationToken cancellationToken)
    {
        if (_playback.GetSnapshot() is { } snapshot)
        {
            await RunPlaybackActionAsync(() => _playback.SeekAsync(snapshot.Position.AddSeconds(-10) < snapshot.Earliest ? snapshot.Earliest : snapshot.Position.AddSeconds(-10), cancellationToken), cancellationToken).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task GoLiveAsync(CancellationToken cancellationToken)
    {
        await RunPlaybackActionAsync(() => _playback.SeekAsync(null, cancellationToken), cancellationToken).ConfigureAwait(true);
        await RefreshPlaybackAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshGuideAsync(CancellationToken cancellationToken)
    {
        _guideService?.RequestRefresh();
        await LoadChannelGuideAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task LoadChannelGuideAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshGuideCoreAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText = Text["GuideFailed"] + " " + exception.Message;
        }
    }

    private async Task RefreshGuideCoreAsync(CancellationToken cancellationToken)
    {
        int request = Interlocked.Increment(ref _guideRequest);
        IReadOnlyList<Programme> captured = _playback.Guide;
        _guideService?.Ingest(captured);
        if (_data is not null)
        {
            if (_guideService is null && captured.Count > 0)
            {
                await _data.SaveProgrammesAsync(captured, cancellationToken).ConfigureAwait(true);
            }

            await RefreshSavedChannelsAsync(cancellationToken).ConfigureAwait(true);
            DateTimeOffset now = _clock.GetUtcNow();
            TvChannel? channel = SelectedChannel ?? _playback.CurrentChannel;
            IReadOnlyList<Programme> entries = channel?.ServiceIdentity is { } identity
                ? await _data.GetProgrammesAsync(identity, now.AddDays(-1), now.AddDays(14), cancellationToken).ConfigureAwait(true)
                : captured;
            if (request != Volatile.Read(ref _guideRequest))
            {
                return;
            }

            _guideEntries = entries;
        }
        else
        {
            _guideEntries = captured;
        }

        if (_guideService is not null)
        {
            GuideStatus status = _guideService.Status;
            GuideStatusText = status.IsScanning
                ? string.Format(CultureInfo.CurrentCulture, Text["GuideScanning"], status.CompletedFrequencies, status.TotalFrequencies)
                : string.Format(CultureInfo.CurrentCulture, Text["GuideCoverage"], status.ProgrammeCount,
                    status.LastRefreshUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? Text["Never"],
                    status.CoverageUntil?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? Text["Unknown"]);
            if (status.LastError is not null)
            {
                GuideStatusText += " " + status.LastError;
            }
        }

        FilterGuide();
    }

    private void FilterGuide()
    {
        TvChannel? channel = SelectedChannel ?? _playback.CurrentChannel;
        int? serviceId = channel?.ServiceId;
        if (string.Equals(channel?.Id, _playback.CurrentChannel?.Id, StringComparison.Ordinal))
        {
            serviceId ??= _playback.CurrentServiceId;
        }

        Programme[] channelGuide = _guideEntries.Where(programme => channel?.ServiceIdentity is { } identity
            ? programme.Service == identity : programme.IsActualTransport && programme.Service.DeviceId == channel?.DeviceId && programme.Service.ServiceId == serviceId).OrderBy(programme => programme.Start).ToArray();
        DateTime day = (GuideDay ?? _clock.GetLocalNow()).Date;
        DateTimeOffset start = ScheduleCalculator.ResolveLocal(day, _clock.LocalTimeZone);
        DateTimeOffset end = ScheduleCalculator.ResolveLocal(day.AddDays(1), _clock.LocalTimeZone);
        GuideTimelineWidth = (end - start).TotalMinutes * 2;
        GuideHours.Clear();
        for (DateTimeOffset hour = start; hour < end; hour = hour.AddHours(1))
        {
            GuideHours.Add(TimeZoneInfo.ConvertTime(hour, _clock.LocalTimeZone).ToString("HH:mm zzz", CultureInfo.CurrentCulture));
        }

        Programme? selected = SelectedProgramme?.Programme;
        Programmes.Clear();
        TimelineProgrammes.Clear();
        DateTimeOffset previousEnd = start;
        foreach (Programme programme in channelGuide.Where(programme => (programme.Title.Contains(GuideSearch, StringComparison.CurrentCultureIgnoreCase)
            || programme.Description.Contains(GuideSearch, StringComparison.CurrentCultureIgnoreCase)) && (GuideSearch.Length > 0 || programme.Start < end && programme.End > start)))
        {
            DateTimeOffset left = programme.Start < start ? start : programme.Start;
            DateTimeOffset right = programme.End > end ? end : programme.End;
            var item = new ProgrammeItem(programme, Math.Max(1, (right - left).TotalMinutes * 2 - 4), (left - previousEnd).TotalMinutes * 2, _clock.LocalTimeZone);
            Programmes.Add(item);
            if (programme.Start < end && programme.End > start)
            {
                TimelineProgrammes.Add(item);
                previousEnd = right;
            }
        }

        SelectedProgramme = selected is null ? null : Programmes.FirstOrDefault(item => item.Programme.Service == selected.Service && item.Programme.EventId == selected.EventId);
        UpdateNowNext(channelGuide);
    }

    private void UpdateNowNext(Programme[] programmes)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        Programme? current = programmes.FirstOrDefault(programme => programme.Start <= now && programme.End > now);
        Programme? next = programmes.FirstOrDefault(programme => programme.Start > now);
        NowNextText = string.Format(CultureInfo.CurrentCulture, Text["NowNext"], current?.Title ?? Text["Unknown"], next?.Title ?? Text["Unknown"]);
    }

    private async Task RefreshSavedChannelsAsync(CancellationToken cancellationToken)
    {
        if (_data is null)
        {
            return;
        }

        IReadOnlyList<TvChannel> channels = await _data.GetChannelsAsync(cancellationToken).ConfigureAwait(true);
        if (!_allChannels.SequenceEqual(channels))
        {
            TvChannel? selected = SelectedChannel is null ? null : await _data.GetChannelAsync(SelectedChannel.Id, cancellationToken).ConfigureAwait(true);
            _allChannels.Clear();
            _allChannels.AddRange(channels);
            FilterChannels();
            SelectedChannel = channels.FirstOrDefault(channel => string.Equals(channel.Id, selected?.Id, StringComparison.Ordinal));
        }
    }

    private Task RefreshPlaybackAsync(CancellationToken cancellationToken)
    {
        ReceptionSnapshot reception = _playback.Reception;
        ReceptionSummary = string.Format(CultureInfo.CurrentCulture, Text["ReceptionSummary"], reception.Tuners.Sum(item => item.ActiveSessions), reception.RtpPackets, reception.LostPackets);
        if (reception.Signal is { } signal)
        {
            ReceptionSummary += " · " + string.Format(CultureInfo.CurrentCulture, Text["SignalSummary"], signal.SignalLevel, signal.SignalQuality);
        }

        PlaybackSnapshot? snapshot = _playback.GetSnapshot();
        HasPlayback = snapshot is not null;
        IsLivePlayback = snapshot?.IsLive ?? false;
        if (snapshot is null)
        {
            TimelineLabel = string.Empty;
            return Task.CompletedTask;
        }

        TimelineMaximum = Math.Max(1, (snapshot.Live - snapshot.Earliest).TotalSeconds);
        if (!_timelineDragging)
        {
            TimelinePosition = Math.Clamp((snapshot.Position - snapshot.Earliest).TotalSeconds, 0, TimelineMaximum);
        }

        _paused = snapshot.IsPaused;
        OnPropertyChanged(nameof(PauseButtonText));
        TimeSpan behind = snapshot.Live - snapshot.Position;
        TimelineLabel = snapshot.IsLive ? behind < TimeSpan.FromSeconds(2) ? Text["Live"]
            : string.Format(CultureInfo.CurrentCulture, Text["BehindLive"], behind.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture))
            : string.Concat((snapshot.Position - snapshot.Earliest).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture), " / ", (snapshot.Live - snapshot.Earliest).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
        if (snapshot.ResumePointExpired && !_expiredNotified)
        {
            StatusText = Text["BufferExpired"];
        }

        _expiredNotified = snapshot.ResumePointExpired;
        if (snapshot.Failure is not null)
        {
            StatusText = Text["ReceptionFailed"] + " " + snapshot.Failure;
        }

        return Task.CompletedTask;
    }

    private async void OnUpdateTick(object? sender, EventArgs args)
    {
        if (_refreshing || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _refreshing = true;
        try
        {
            await RefreshPlaybackAsync(_viewModelLifetime.Token).ConfigureAwait(true);
            if (_guideTicks++ % 10 == 0)
            {
                await RefreshGuideCoreAsync(_viewModelLifetime.Token).ConfigureAwait(true);
            }

            if (_recordingTicks++ % 5 == 0)
            {
                await RefreshRecordingsAsync(_viewModelLifetime.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (_viewModelLifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText = Text["ReceptionFailed"] + " " + exception.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RunPlaybackActionAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText = Text["PlaybackFailed"] + " " + exception.Message;
        }
    }

    private void OnPlaybackTracksChanged(object? sender, EventArgs args) => Dispatcher.UIThread.Post(RefreshTracks);

    private void OnPlaybackFailed(object? sender, EventArgs args) => Dispatcher.UIThread.Post(() => StatusText = Text["PlaybackFailed"]);
}
