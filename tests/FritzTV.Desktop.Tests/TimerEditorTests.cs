using FritzTV.Core.Models;
using FritzTV.Desktop.Localization;
using FritzTV.Desktop.Services;
using FritzTV.Desktop.ViewModels;
using FritzTV.Infrastructure.Devices;
using FritzTV.Infrastructure.Persistence;
using FritzTV.Infrastructure.Recording;
using FritzTV.Infrastructure.Transport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FritzTV.Desktop.Tests;

[Collection("Headless")]
public sealed class TimerEditorTests(HeadlessFixture fixture)
{
    [Fact]
    public async Task ManualTimerPersistsWeekdaysPaddingAndDstGapResolution()
    {
        await fixture.Session.Dispatch(async () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "fritztv-editor-" + Guid.NewGuid().ToString("N"));
            try
            {
                var repository = new AppRepository(Path.Combine(directory, "app.db"));
                await repository.InitializeAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                var channel = new TvChannel("synthetic", Guid.NewGuid(), "Synthetic", ChannelKind.Television, new Uri("rtsp://192.0.2.1/?freq=100&pids=0,1"));
                await repository.SaveChannelsAsync([channel], TestContext.Current.CancellationToken).ConfigureAwait(true);
                var sessions = new SatIpSessionManager();
                await using var sessionLifetime = sessions.ConfigureAwait(false);
                var clock = new EditorClock();
                var recordings = new RecordingCoordinator(repository, sessions, clock);
                await using var recordingLifetime = recordings.ConfigureAwait(false);
                using var http = new HttpClient(new RejectNetworkHandler());
                using var viewModel = new MainWindowViewModel(new SatIpDeviceClient(http), new SsdpDiscovery(), new FakePlaybackController(), new UiText(), new DesktopDataService(repository), clock, recordings: recordings)
                {
                    SelectedChannel = channel,
                    TimerTitle = "DST gap programme",
                    TimerStartDate = new DateTimeOffset(2030, 3, 31, 0, 0, 0, TimeSpan.Zero),
                    TimerEndDate = new DateTimeOffset(2030, 3, 31, 0, 0, 0, TimeSpan.Zero),
                    TimerStartTime = new TimeSpan(2, 30, 0),
                    TimerEndTime = new TimeSpan(4, 0, 0),
                    RepeatSunday = true,
                    RepeatMonday = true,
                    TimerRetainSource = true,
                };
                await viewModel.ScheduleTimerCommand.ExecuteAsync(null).ConfigureAwait(true);
                RecordingDefinition definition = Assert.Single(await repository.GetDefinitionsAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
                Assert.Equal(new DateTimeOffset(2030, 3, 31, 1, 30, 0, TimeSpan.Zero), definition.Start);
                Assert.Equal(new DateTimeOffset(2030, 3, 31, 3, 0, 0, TimeSpan.Zero), definition.End);
                Assert.Equal(Weekdays.Sunday | Weekdays.Monday, definition.RepeatOn);
                Assert.Equal(TimeSpan.FromMinutes(2), definition.EffectiveStartPadding);
                Assert.Equal(TimeSpan.FromMinutes(5), definition.EffectiveEndPadding);
                Assert.True(definition.RetainTransportStream);
                Assert.Equal(new DateTime(2030, 3, 31, 2, 30, 0, DateTimeKind.Unspecified), definition.RecurrenceLocalStart);
                Assert.Single(viewModel.SavedTimers);
                Assert.NotEmpty(viewModel.Recordings);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(directory, recursive: true);
            }

            return 0;
        }, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    [Fact]
    public async Task DistantEpgTimerPreservesSecondAutumnHourAndRemainsCancellable()
    {
        await fixture.Session.Dispatch(async () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "fritztv-editor-" + Guid.NewGuid().ToString("N"));
            try
            {
                var repository = new AppRepository(Path.Combine(directory, "app.db"));
                await repository.InitializeAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                Guid device = Guid.NewGuid();
                var identity = new DvbServiceIdentity(device, 1, 2, 3);
                var channel = new TvChannel("synthetic", device, "Synthetic", ChannelKind.Television, new Uri("rtsp://192.0.2.1/?freq=100&pids=0,1"), 3, ServiceIdentity: identity);
                await repository.SaveChannelsAsync([channel], TestContext.Current.CancellationToken).ConfigureAwait(true);
                var sessions = new SatIpSessionManager();
                await using var sessionLifetime = sessions.ConfigureAwait(false);
                var clock = new EditorClock();
                var recordings = new RecordingCoordinator(repository, sessions, clock);
                await using var recordingLifetime = recordings.ConfigureAwait(false);
                using var http = new HttpClient(new RejectNetworkHandler());
                using var viewModel = new MainWindowViewModel(new SatIpDeviceClient(http), new SsdpDiscovery(), new FakePlaybackController(), new UiText(), new DesktopDataService(repository), clock, recordings: recordings) { SelectedChannel = channel };
                var start = new DateTimeOffset(2030, 10, 27, 1, 30, 0, TimeSpan.Zero);
                var programme = new Programme(identity, 7, start, TimeSpan.FromMinutes(30), "Second autumn hour", "Synthetic distant programme", "eng");
                viewModel.SelectedProgramme = new ProgrammeItem(programme);
                viewModel.UseProgrammeForTimerCommand.Execute(null);
                await viewModel.ScheduleTimerCommand.ExecuteAsync(null).ConfigureAwait(true);
                TimerItem saved = Assert.Single(viewModel.SavedTimers);
                Assert.Equal(start, saved.Definition.Start);
                Assert.Equal(programme.End, saved.Definition.End);
                Assert.Equal(programme.EventId, saved.Definition.EventId);
                viewModel.SelectedTimer = saved;
                await viewModel.DeleteSavedTimerCommand.ExecuteAsync(null).ConfigureAwait(true);
                Assert.Empty(viewModel.SavedTimers);
                Assert.Empty(await repository.GetDefinitionsAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(directory, recursive: true);
            }

            return 0;
        }, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    [Fact]
    public async Task InvalidTimerShowsValidationWithoutWritingDatabase()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var http = new HttpClient(new RejectNetworkHandler());
            var text = new UiText();
            using var viewModel = new MainWindowViewModel(new SatIpDeviceClient(http), new SsdpDiscovery(), new FakePlaybackController(), text);
            await viewModel.ScheduleTimerCommand.ExecuteAsync(null).ConfigureAwait(true);
            Assert.Equal(text["InvalidTimer"], viewModel.StatusText);
            Assert.Empty(viewModel.Recordings);
            return 0;
        }, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    private sealed class EditorClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2030, 3, 30, 12, 0, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    }
}
