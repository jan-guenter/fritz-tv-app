using FritzTV.Core.Models;
using FritzTV.Desktop.Localization;
using FritzTV.Desktop.Services;
using FritzTV.Desktop.ViewModels;
using FritzTV.Infrastructure.Devices;
using FritzTV.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FritzTV.Desktop.Tests;

[Collection("Headless")]
public sealed class RecordingPlaybackTests(HeadlessFixture fixture)
{
    [Fact]
    public async Task RetainedTransportPlaybackResolvesTheArchivedChannelService()
    {
        await fixture.Session.Dispatch(async () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "fritztv-file-playback-" + Guid.NewGuid().ToString("N"));
            try
            {
                var repository = new AppRepository(Path.Combine(directory, "app.db"));
                await repository.InitializeAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                var channel = new TvChannel("archived", Guid.NewGuid(), "Archived", ChannelKind.Television,
                    new Uri("rtsp://192.0.2.1/?freq=100&pids=0,1"), ServiceId: 1234, IsAvailable: false);
                await repository.SaveChannelsAsync([channel], TestContext.Current.CancellationToken).ConfigureAwait(true);
                string source = Path.Combine(directory, "retained.ts");
                await File.WriteAllBytesAsync(source, [0x47], TestContext.Current.CancellationToken).ConfigureAwait(true);
                var recording = new RecordingInstance(Guid.NewGuid(), Guid.NewGuid(), channel.Id, "Retained source",
                    DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), DateTimeOffset.UnixEpoch,
                    RecordingState.Partial, source, Path.Combine(directory, "missing.mkv"));
                var playback = new FakePlaybackController();
                using var http = new HttpClient(new RejectNetworkHandler());
                var text = new UiText();
                using var viewModel = new MainWindowViewModel(new SatIpDeviceClient(http), new SsdpDiscovery(), playback, text, new DesktopDataService(repository))
                {
                    SelectedRecording = new RecordingItem(recording, text),
                };
                await viewModel.PlayRecordingCommand.ExecuteAsync(null).ConfigureAwait(true);
                Assert.Equal(source, playback.LastFilePath);
                Assert.Equal(1234, playback.LastFileServiceId);
                Assert.Empty(await repository.GetChannelsAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(directory, recursive: true);
            }

            return 0;
        }, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
}
