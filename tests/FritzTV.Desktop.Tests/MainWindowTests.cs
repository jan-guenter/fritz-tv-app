using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using FritzTV.Desktop.Localization;
using FritzTV.Desktop.ViewModels;
using FritzTV.Infrastructure.Devices;
using Xunit;

namespace FritzTV.Desktop.Tests;

[Collection("Headless")]
public sealed class MainWindowTests(HeadlessFixture fixture)
{
    [Fact]
    public async Task InvalidDescriptorIsRejectedBeforeNetworkAccess()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var http = new HttpClient(new RejectNetworkHandler());
            var text = new UiText();
            text.SetLanguage(german: false);
            var viewModel = new MainWindowViewModel(new SatIpDeviceClient(http), new SsdpDiscovery(), new FakePlaybackController(), text)
            {
                DescriptorUrl = "file:///etc/passwd",
            };
            await viewModel.ImportCommand.ExecuteAsync(null).ConfigureAwait(true);
            Assert.Equal(text["InvalidDescriptor"], viewModel.StatusText);
            return 0;
        }, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    [Fact]
    public async Task InvalidTeletextDoesNotReachNativePlayer()
    {
        await fixture.Session.Dispatch(() =>
        {
            using var http = new HttpClient(new RejectNetworkHandler());
            var playback = new FakePlaybackController();
            var text = new UiText();
            var viewModel = new MainWindowViewModel(new SatIpDeviceClient(http), new SsdpDiscovery(), playback, text)
            {
                TeletextPage = "999",
            };
            viewModel.ShowTeletextCommand.Execute(null);
            Assert.Null(playback.TeletextPage);
            Assert.Equal(text["InvalidTeletext"], viewModel.StatusText);
            viewModel.TeletextPage = "150";
            viewModel.ShowTeletextCommand.Execute(null);
            Assert.Equal(150, playback.TeletextPage);
            viewModel.CloseTeletextCommand.Execute(null);
            Assert.Equal(0, playback.TeletextPage);
        }, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    [Fact]
    public async Task LanguageSwitchUpdatesResourcesAndVisibleWindowLabels()
    {
        await fixture.Session.Dispatch(() =>
        {
            using var http = new HttpClient(new RejectNetworkHandler());
            var text = new UiText();
            text.SetLanguage(german: false);
            var viewModel = new MainWindowViewModel(new SatIpDeviceClient(http), new SsdpDiscovery(), new FakePlaybackController(), text);
            var window = new MainWindow { DataContext = viewModel };
            window.Show();
            Button play = window.GetVisualDescendants().OfType<Button>().Single(button => string.Equals(AutomationProperties.GetAutomationId(button), "Play", StringComparison.Ordinal));
            Assert.Equal("Play", play.Content);
            Assert.Equal("FRITZ TV", window.Title);
            Assert.Equal("MainWindow", AutomationProperties.GetAutomationId(window));
            viewModel.German = true;
            Assert.Equal("Wiedergabe", text["Play"]);
            Assert.Equal("Wiedergabe", play.Content);
            Assert.Equal("Alle Sender", viewModel.ChannelFilters[0]);
            viewModel.German = false;
            Assert.Equal("Play", text["Play"]);
            window.Close();
        }, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    [Fact]
    public async Task VolumeIsClampedBeforePassingToNativePlayer()
    {
        await fixture.Session.Dispatch(() =>
        {
            using var http = new HttpClient(new RejectNetworkHandler());
            var playback = new FakePlaybackController();
            var viewModel = new MainWindowViewModel(new SatIpDeviceClient(http), new SsdpDiscovery(), playback, new UiText()) { Volume = 500 };
            Assert.Equal(100, playback.Volume);
            viewModel.Volume = -20;
            Assert.Equal(0, playback.Volume);
        }, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
}
