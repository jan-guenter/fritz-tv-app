using FritzTV.Core.Models;
using FritzTV.Desktop.Localization;
using FritzTV.Desktop.ViewModels;
using FritzTV.Infrastructure.Devices;
using Xunit;

namespace FritzTV.Desktop.Tests;

[Collection("Headless")]
public sealed class GuideViewModelTests(HeadlessFixture fixture)
{
    [Fact]
    public async Task GuideUsesCompleteServiceIdentityAndTimelineRepresentsDstDay()
    {
        await fixture.Session.Dispatch(async () =>
        {
            Guid device = Guid.NewGuid();
            var identity = new DvbServiceIdentity(device, 1, 2, 7);
            var selected = new TvChannel("selected", device, "Selected", ChannelKind.Television, new Uri("rtsp://192.0.2.1/?freq=100&pids=0,1"), 7, ServiceIdentity: identity);
            var start = new DateTimeOffset(2030, 3, 30, 12, 0, 0, TimeSpan.Zero);
            var programme = new Programme(identity, 1, start, TimeSpan.FromHours(1), "Current programme", "Complete extended description", "eng");
            var foreign = programme with { Service = new DvbServiceIdentity(device, 1, 9, 7), Title = "Other multiplex" };
            var playback = new FakePlaybackController { Guide = [programme, foreign], CurrentChannel = selected };
            using var http = new HttpClient(new RejectNetworkHandler());
            using var viewModel = new MainWindowViewModel(new SatIpDeviceClient(http), new SsdpDiscovery(), playback, new UiText(), timeProvider: new GuideClock()) { SelectedChannel = selected };
            await viewModel.RefreshGuideCommand.ExecuteAsync(null).ConfigureAwait(true);
            ProgrammeItem item = Assert.Single(viewModel.Programmes);
            Assert.Equal(programme, item.Programme);
            Assert.Equal(116, Assert.Single(viewModel.TimelineProgrammes).TimelineWidth);
            viewModel.SelectedProgramme = item;
            viewModel.RepeatDailyCommand.Execute(null);
            viewModel.UseProgrammeForTimerCommand.Execute(null);
            Assert.Equal(programme.Title, viewModel.TimerTitle);
            Assert.False(viewModel.RepeatSunday);
            Assert.False(viewModel.RepeatMonday);
            Assert.Equal(programme.Description, viewModel.SelectedProgramme?.Description);
            Assert.Contains(programme.Title, viewModel.NowNextText, StringComparison.Ordinal);
            viewModel.GuideDay = new DateTimeOffset(2030, 3, 31, 0, 0, 0, TimeSpan.Zero);
            Assert.Equal(23, viewModel.GuideHours.Count);
            Assert.Equal(23 * 60 * 2, viewModel.GuideTimelineWidth);
            Assert.DoesNotContain(viewModel.GuideHours, hour => hour.StartsWith("02:00", StringComparison.Ordinal));
            Assert.Empty(viewModel.TimelineProgrammes);
            return 0;
        }, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    private sealed class GuideClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2030, 3, 30, 12, 30, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    }
}
