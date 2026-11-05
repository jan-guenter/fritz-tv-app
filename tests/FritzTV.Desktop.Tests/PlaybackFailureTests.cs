using FritzTV.Core.Models;
using FritzTV.Desktop.Localization;
using FritzTV.Desktop.ViewModels;
using FritzTV.Infrastructure.Devices;
using Xunit;

namespace FritzTV.Desktop.Tests;

[Collection("Headless")]
public sealed class PlaybackFailureTests(HeadlessFixture fixture)
{
    [Fact]
    public async Task HttpTransportTimeoutBecomesVisibleImportFailure()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var http = new HttpClient(new TimeoutHandler());
            var text = new UiText();
            using var viewModel = new MainWindowViewModel(new SatIpDeviceClient(http), new SsdpDiscovery(), new FakePlaybackController(), text)
            {
                DescriptorUrl = "http://192.0.2.1/satipdesc.xml",
            };
            await viewModel.ImportCommand.ExecuteAsync(null).ConfigureAwait(true);
            Assert.StartsWith(text["ImportFailed"], viewModel.StatusText, StringComparison.Ordinal);
            Assert.True(viewModel.StatusText.Length > text["ImportFailed"].Length, "The import error should include an actionable transport diagnostic.");
            return 0;
        }, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    [Fact]
    public async Task InternalReceptionCancellationBecomesVisiblePlaybackFailure()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var http = new HttpClient(new RejectNetworkHandler());
            var text = new UiText();
            var playback = new FakePlaybackController { PlayFailure = new OperationCanceledException("synthetic reception deadline") };
            using var viewModel = new MainWindowViewModel(new SatIpDeviceClient(http), new SsdpDiscovery(), playback, text)
            {
                SelectedChannel = new TvChannel("synthetic", Guid.NewGuid(), "Synthetic", ChannelKind.Radio, new Uri("rtsp://192.0.2.1/?freq=100&pids=0,1")),
            };
            await viewModel.PlayCommand.ExecuteAsync(null).ConfigureAwait(true);
            Assert.StartsWith(text["PlaybackFailed"], viewModel.StatusText, StringComparison.Ordinal);
            Assert.Contains("synthetic reception deadline", viewModel.StatusText, StringComparison.Ordinal);
            return 0;
        }, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("synthetic transport timeout"));
    }
}
