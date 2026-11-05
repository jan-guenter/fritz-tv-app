using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using FritzTV.Core.Models;
using FritzTV.Desktop.Services;

namespace FritzTV.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _viewModelLifetime = new();
    private DispatcherTimer? _updateTimer;
    private bool _refreshing;
    private bool _timelineDragging;
    private bool _paused;
    private bool _hasPlayback;
    private bool _expiredNotified;
    private double _timelinePosition;
    private double _timelineMaximum = 1;
    private string _timelineLabel = string.Empty;
    private int _guideTicks;
    private string _guideSearch = string.Empty;
    private ProgrammeItem? _selectedProgramme;
    private IReadOnlyList<Programme> _guideEntries = [];
    private int _disposed;

    public ObservableCollection<ProgrammeItem> Programmes { get; } = [];

    public string PauseButtonText => Text[_paused ? "Resume" : "Pause"];

    public bool HasPlayback
    {
        get => _hasPlayback;
        private set => SetProperty(ref _hasPlayback, value);
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

        _viewModelLifetime.Cancel();
        _viewModelLifetime.Dispose();
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
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                StatusText = Text["PlaybackFailed"] + " " + exception.Message;
            }
        }

        EndTimelineDrag();
    }

    [RelayCommand]
    private async Task GoLiveAsync(CancellationToken cancellationToken)
    {
        await _playback.SeekAsync(null, cancellationToken).ConfigureAwait(true);
        await RefreshPlaybackAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshGuideAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Programme> captured = _playback.Guide;
        if (_repository is not null)
        {
            if (captured.Count > 0)
            {
                await _repository.SaveProgrammesAsync(captured, cancellationToken).ConfigureAwait(true);
            }

            DateTimeOffset now = _clock.GetUtcNow();
            _guideEntries = await _repository.GetProgrammesAsync(now.AddDays(-1), now.AddDays(14), cancellationToken).ConfigureAwait(true);
        }
        else
        {
            _guideEntries = captured;
        }

        FilterGuide();
    }

    private void FilterGuide()
    {
        TvChannel? channel = SelectedChannel ?? _playback.CurrentChannel;
        int? serviceId = channel?.ServiceId;
        if (channel?.Id is { } id && string.Equals(id, _playback.CurrentChannel?.Id, StringComparison.Ordinal))
        {
            serviceId ??= _playback.CurrentServiceId;
        }

        Programmes.Clear();
        foreach (Programme programme in _guideEntries.Where(programme => programme.Service.DeviceId == channel?.DeviceId
            && programme.Service.ServiceId == serviceId && (programme.Title.Contains(GuideSearch, StringComparison.CurrentCultureIgnoreCase)
                || programme.Description.Contains(GuideSearch, StringComparison.CurrentCultureIgnoreCase))))
        {
            Programmes.Add(new ProgrammeItem(programme));
        }
    }

    private async Task RefreshPlaybackAsync(CancellationToken cancellationToken)
    {
        PlaybackSnapshot? snapshot = _playback.GetSnapshot();
        HasPlayback = snapshot is not null;
        if (snapshot is null)
        {
            TimelineLabel = string.Empty;
            return;
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

        if (_guideTicks++ % 10 == 0)
        {
            await RefreshGuideAsync(cancellationToken).ConfigureAwait(true);
        }
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

    private void OnPlaybackTracksChanged(object? sender, EventArgs args) => Dispatcher.UIThread.Post(RefreshTracks);

    private void OnPlaybackFailed(object? sender, EventArgs args) => Dispatcher.UIThread.Post(() => StatusText = Text["PlaybackFailed"]);
}
