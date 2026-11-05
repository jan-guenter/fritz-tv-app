using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FritzTV.Core.Models;
using FritzTV.Core.Scheduling;
using FritzTV.Desktop.Services;
using FritzTV.Infrastructure.Recording;

namespace FritzTV.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly RecordingCoordinator? _recordings;
    private readonly RecordingActions? _recordingActions;
    private ushort? _timerEventId;
    private Programme? _timerProgramme;
    private int _recordingTicks;

    [ObservableProperty] private RecordingItem? _selectedRecording;
    [ObservableProperty] private TimerItem? _selectedTimer;
    [ObservableProperty] private int _instantStartMode;
    [ObservableProperty] private string _timerTitle = string.Empty;
    [ObservableProperty] private DateTimeOffset? _timerStartDate;
    [ObservableProperty] private DateTimeOffset? _timerEndDate;
    [ObservableProperty] private TimeSpan? _timerStartTime;
    [ObservableProperty] private TimeSpan? _timerEndTime;
    [ObservableProperty] private int _timerStartPadding = 2;
    [ObservableProperty] private int _timerEndPadding = 5;
    [ObservableProperty] private bool _repeatMonday;
    [ObservableProperty] private bool _repeatTuesday;
    [ObservableProperty] private bool _repeatWednesday;
    [ObservableProperty] private bool _repeatThursday;
    [ObservableProperty] private bool _repeatFriday;
    [ObservableProperty] private bool _repeatSaturday;
    [ObservableProperty] private bool _repeatSunday;
    [ObservableProperty] private bool _timerRetainSource;
    [ObservableProperty] private bool _detailsExpanded;
    [ObservableProperty] private int _detailsTab;

    public ObservableCollection<RecordingItem> Recordings { get; } = [];

    public ObservableCollection<TimerItem> SavedTimers { get; } = [];

    public IReadOnlyList<string> InstantStartModes => [Text["RecordFromNow"], Text["RecordFromProgramme"], Text["RecordFromOldest"]];

    public string TimerTimeZone => _clock.LocalTimeZone.DisplayName;

    private void InitializeRecordingEditor()
    {
        InitializeRecordingEditorTimes();
        if (_recordings is not null)
        {
            _recordings.Changed += OnRecordingsChanged;
        }
    }

    private void InitializeRecordingEditorTimes()
    {
        DateTimeOffset upcoming = TimeZoneInfo.ConvertTime(_clock.GetUtcNow().AddMinutes(5), _clock.LocalTimeZone);
        var local = new DateTimeOffset(upcoming.Year, upcoming.Month, upcoming.Day, upcoming.Hour, upcoming.Minute, 0, upcoming.Offset);
        TimerStartDate = local;
        TimerStartTime = local.TimeOfDay;
        TimerEndDate = local.AddHours(1);
        TimerEndTime = local.AddHours(1).TimeOfDay;
    }

    [RelayCommand]
    private async Task RefreshRecordingsAsync(CancellationToken cancellationToken)
    {
        if (_recordings is null)
        {
            return;
        }

        try
        {
            Guid? selected = SelectedRecording?.Recording.Id;
            IReadOnlyList<RecordingInstance> snapshots = await _recordings.GetRecordingsAsync(cancellationToken).ConfigureAwait(true);
            IReadOnlyList<RecordingDefinition> definitions = _data is null ? [] : await _data.GetDefinitionsAsync(cancellationToken).ConfigureAwait(true);
            Guid? selectedTimer = SelectedTimer?.Definition.Id;
            SavedTimers.Clear();
            foreach (RecordingDefinition definition in definitions.OrderBy(item => item.Start))
            {
                SavedTimers.Add(new TimerItem(definition, Text));
            }

            SelectedTimer = SavedTimers.FirstOrDefault(item => item.Definition.Id == selectedTimer);
            Recordings.Clear();
            foreach (RecordingInstance recording in snapshots)
            {
                Recordings.Add(new RecordingItem(recording, Text));
            }

            SelectedRecording = Recordings.FirstOrDefault(item => item.Recording.Id == selected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText = Text["RecordingFailed"] + " " + exception.Message;
        }
    }

    [RelayCommand]
    private async Task RecordInstantAsync(CancellationToken cancellationToken)
    {
        TvChannel? channel = SelectedChannel ?? _playback.CurrentChannel;
        if (channel is null || _recordingActions is null)
        {
            StatusText = Text["SelectChannel"];
            return;
        }

        if (InstantStartMode > 0 && (!string.Equals(channel.Id, _playback.CurrentChannel?.Id, StringComparison.Ordinal) || _playback.GetSnapshot() is not { IsLive: true }))
        {
            StatusText = Text["BufferedRecordingUnavailable"];
            return;
        }

        await RunRecordingActionAsync(async () =>
        {
            await _recordingActions.StartAsync(channel, InstantStartMode, cancellationToken).ConfigureAwait(true);
            ShowRecordings();
        }, cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private void UseProgrammeForTimer()
    {
        if (SelectedProgramme?.Programme is not { } programme)
        {
            return;
        }

        TimerTitle = programme.Title;
        ResetTimerOptions();
        DateTimeOffset start = TimeZoneInfo.ConvertTime(programme.Start, _clock.LocalTimeZone);
        DateTimeOffset end = TimeZoneInfo.ConvertTime(programme.End, _clock.LocalTimeZone);
        TimerStartDate = start;
        TimerStartTime = start.TimeOfDay;
        TimerEndDate = end;
        TimerEndTime = end.TimeOfDay;
        _timerEventId = programme.EventId;
        _timerProgramme = programme;
        ShowRecordings();
    }

    [RelayCommand]
    private async Task ScheduleTimerAsync(CancellationToken cancellationToken)
    {
        TvChannel? channel = SelectedChannel ?? _playback.CurrentChannel;
        if (channel is null || _recordings is null || string.IsNullOrWhiteSpace(TimerTitle)
            || TimerStartDate is null || TimerEndDate is null || TimerStartTime is null || TimerEndTime is null
            || TimerStartPadding < 0 || TimerEndPadding < 0)
        {
            StatusText = Text["InvalidTimer"];
            return;
        }

        DateTime localStart = TimerStartDate.Value.Date + TimerStartTime.Value;
        DateTime localEnd = TimerEndDate.Value.Date + TimerEndTime.Value;
        if (localEnd <= localStart)
        {
            StatusText = Text["InvalidTimer"];
            return;
        }

        Weekdays repeat = GetRepeatDays();
        (DateTimeOffset start, DateTimeOffset end) = repeat == Weekdays.None && _timerProgramme is { } programme
            ? (programme.Start, programme.End)
            : ScheduleCalculator.ResolveLocalInterval(localStart, localEnd, _clock.LocalTimeZone);
        if (end <= start || end <= _clock.GetUtcNow())
        {
            StatusText = Text["InvalidTimer"];
            return;
        }

        var definition = new RecordingDefinition(Guid.NewGuid(), channel.Id, TimerTitle.Trim(), start, end, _clock.LocalTimeZone.Id,
            repeat, TimeSpan.FromMinutes(TimerStartPadding), TimeSpan.FromMinutes(TimerEndPadding), repeat == Weekdays.None ? _timerEventId : null, TimerRetainSource,
            RecurrenceLocalStart: repeat == Weekdays.None ? null : localStart, RecurrenceLocalEnd: repeat == Weekdays.None ? null : localEnd);
        await RunRecordingActionAsync(async () =>
        {
            await _recordings.CreateTimerAsync(definition, cancellationToken).ConfigureAwait(true);
            StatusText = Text["TimerSaved"];
        }, cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task StopRecordingAsync(CancellationToken cancellationToken)
    {
        if (SelectedRecording is { } selected && _recordings is not null)
        {
            await RunRecordingActionAsync(() => _recordings.StopAsync(selected.Recording.Id, cancellationToken), cancellationToken).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ExtendRecordingAsync(CancellationToken cancellationToken)
    {
        if (SelectedRecording is { } selected && _recordings is not null && selected.Recording.End != DateTimeOffset.MaxValue)
        {
            await RunRecordingActionAsync(() => _recordings.ExtendAsync(selected.Recording.Id, selected.Recording.End.AddMinutes(10), cancellationToken), cancellationToken).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task CancelTimerAsync(CancellationToken cancellationToken)
    {
        if (SelectedRecording is { } selected && _recordings is not null)
        {
            await RunRecordingActionAsync(() => _recordings.CancelTimerAsync(selected.Recording.DefinitionId, cancellationToken), cancellationToken).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DeleteSavedTimerAsync(CancellationToken cancellationToken)
    {
        if (SelectedTimer is { } timer && _recordings is not null)
        {
            await RunRecordingActionAsync(() => _recordings.CancelTimerAsync(timer.Definition.Id, cancellationToken), cancellationToken).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task RetryFinalizationAsync(CancellationToken cancellationToken)
    {
        if (SelectedRecording is { } selected && _recordings is not null)
        {
            await RunRecordingActionAsync(() => _recordings.RetryFinalizeAsync(selected.Recording.Id, cancellationToken), cancellationToken).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task PlayRecordingAsync(CancellationToken cancellationToken)
    {
        RecordingInstance? selected = SelectedRecording?.Recording;
        string? path = File.Exists(selected?.OutputPath) ? selected.OutputPath : selected?.SourcePath;
        if (path is null || !File.Exists(path))
        {
            StatusText = Text["RecordingFileMissing"];
            return;
        }

        DetailsExpanded = false;
        await RunRecordingActionAsync(() => _playback.PlayFileAsync(path, cancellationToken), cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private void NewManualTimer()
    {
        InitializeRecordingEditorTimes();
        TimerTitle = string.Empty;
        ResetTimerOptions();
        ShowRecordings();
    }

    private void ResetTimerOptions()
    {
        RepeatMonday = RepeatTuesday = RepeatWednesday = RepeatThursday = RepeatFriday = RepeatSaturday = RepeatSunday = false;
        TimerStartPadding = (int)_settings.DefaultStartPadding.TotalMinutes;
        TimerEndPadding = (int)_settings.DefaultEndPadding.TotalMinutes;
        TimerRetainSource = _settings.RetainTransportStreams;
        ClearProgrammeLink();
    }

    [RelayCommand]
    private void RepeatDaily()
    {
        RepeatMonday = RepeatTuesday = RepeatWednesday = RepeatThursday = RepeatFriday = RepeatSaturday = RepeatSunday = true;
    }

    private async Task RunRecordingActionAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        try
        {
            await action().ConfigureAwait(true);
            await RefreshRecordingsAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText = Text["RecordingFailed"] + " " + exception.Message;
        }
    }

    private Weekdays GetRepeatDays() => (RepeatMonday ? Weekdays.Monday : Weekdays.None) | (RepeatTuesday ? Weekdays.Tuesday : Weekdays.None)
        | (RepeatWednesday ? Weekdays.Wednesday : Weekdays.None) | (RepeatThursday ? Weekdays.Thursday : Weekdays.None)
        | (RepeatFriday ? Weekdays.Friday : Weekdays.None) | (RepeatSaturday ? Weekdays.Saturday : Weekdays.None) | (RepeatSunday ? Weekdays.Sunday : Weekdays.None);

    private void ShowRecordings()
    {
        DetailsExpanded = true;
        DetailsTab = 1;
    }

    private void OnRecordingsChanged(object? sender, EventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            RefreshRecordingsCommand.Execute(null);
        }
    });

    private void ClearProgrammeLink()
    {
        _timerEventId = null;
        _timerProgramme = null;
    }

    partial void OnDetailsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(VideoRowHeight));
        OnPropertyChanged(nameof(ShowAuxiliaryControls));
    }

    partial void OnTimerStartDateChanged(DateTimeOffset? value) => ClearProgrammeLink();

    partial void OnTimerEndDateChanged(DateTimeOffset? value) => ClearProgrammeLink();

    partial void OnTimerStartTimeChanged(TimeSpan? value) => ClearProgrammeLink();

    partial void OnTimerEndTimeChanged(TimeSpan? value) => ClearProgrammeLink();
}
