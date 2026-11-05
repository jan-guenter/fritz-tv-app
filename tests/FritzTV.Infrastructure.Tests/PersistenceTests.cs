using FritzTV.Core.Models;
using FritzTV.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task RefreshPreservesFavouritesAndGuideUpdatesReplaceEvent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fritztv-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var repository = new AppRepository(Path.Combine(directory, "app.db"));
            await repository.InitializeAsync(cancellationToken);
            await repository.InitializeAsync(cancellationToken);
            var channel = new TvChannel("channel", Guid.NewGuid(), "Synthetic channel", ChannelKind.Television, new Uri("rtsp://192.0.2.1/?pids=0,18,100"));
            await repository.SaveChannelsAsync([channel], cancellationToken);
            await repository.SetFavoriteAsync(channel.Id, true, cancellationToken);
            await repository.SaveChannelsAsync([channel with { Name = "Renamed" }], cancellationToken);
            var restored = Assert.Single(await repository.GetChannelsAsync(cancellationToken));
            Assert.True(restored.IsFavorite);
            Assert.Equal("Renamed", restored.Name);
            var start = new DateTimeOffset(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
            var programme = new Programme(new DvbServiceIdentity(channel.DeviceId, 1, 2, 3), 42, start, TimeSpan.FromHours(1), "Test", "Description", "eng");
            await repository.SaveProgrammesAsync([programme], cancellationToken);
            await repository.SaveProgrammesAsync([programme with { Start = start.AddMinutes(15) }], cancellationToken);
            Assert.Equal(start.AddMinutes(15), Assert.Single(await repository.GetProgrammesAsync(start, start.AddHours(2), cancellationToken)).Start);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
