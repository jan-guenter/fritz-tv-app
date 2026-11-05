using System.Text.Json;
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
            await repository.SaveProgrammesAsync([programme with { Service = programme.Service with { TransportStreamId = 4 } }], cancellationToken);
            Assert.Single(await repository.GetProgrammesAsync(programme.Service, start, start.AddHours(2), cancellationToken));
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
    [Fact]
    public async Task LegacyPaddingMigrationIsAtomicIdempotentAndLeavesActiveCaptureTimesFixed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fritztv-padding-tests-" + Guid.NewGuid().ToString("N"));
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            var repository = new AppRepository(Path.Combine(directory, "app.db"));
            await repository.InitializeAsync(cancellationToken);
            var start = new DateTimeOffset(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
            var definition = new RecordingDefinition(Guid.NewGuid(), "channel", "Test", start, start.AddHours(1), "Europe/Berlin");
            await repository.SaveDefinitionAsync(definition, cancellationToken);
            var scheduled = new RecordingInstance(Guid.NewGuid(), definition.Id, definition.ChannelId, definition.Title, start.AddMinutes(-2), start.AddHours(1).AddMinutes(5), start, RecordingState.Scheduled);
            var active = scheduled with { Id = Guid.NewGuid(), State = RecordingState.Recording };
            await repository.SaveRecordingAsync(scheduled, cancellationToken);
            await repository.SaveRecordingAsync(active, cancellationToken);
            var resolved = definition with { StartPadding = TimeSpan.FromMinutes(4), EndPadding = TimeSpan.FromMinutes(7) };
            await repository.ResolveDefinitionPaddingAsync(resolved, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), cancellationToken);
            await repository.ResolveDefinitionPaddingAsync(resolved, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), cancellationToken);
            var recordings = await repository.GetRecordingsAsync(cancellationToken);
            var updated = Assert.Single(recordings, recording => recording.Id == scheduled.Id);
            Assert.Equal(start.AddMinutes(-4), updated.Start);
            Assert.Equal(start.AddHours(1).AddMinutes(7), updated.End);
            Assert.Equal(active, Assert.Single(recordings, recording => recording.Id == active.Id));
            Assert.Equal(resolved, Assert.Single(await repository.GetDefinitionsAsync(cancellationToken)));
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

    [Fact]
    public async Task SchemaTwoMigratesWithBackupAndPreservesChannels()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fritztv-migration-tests-" + Guid.NewGuid().ToString("N"));
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "app.db");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                await connection.OpenAsync(cancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE channels(id TEXT PRIMARY KEY,device_id TEXT NOT NULL,favourite INTEGER NOT NULL DEFAULT 0,payload TEXT NOT NULL); PRAGMA user_version=2; INSERT INTO channels VALUES('channel','device',1,$payload);";
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new TvChannel("channel", Guid.NewGuid(), "Previous channel", ChannelKind.Radio, new Uri("rtsp://192.0.2.1/?pids=100"))));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var repository = new AppRepository(path);
            await repository.InitializeAsync(cancellationToken);
            Assert.True(Assert.Single(await repository.GetChannelsAsync(cancellationToken)).IsFavorite);
            Assert.True(File.Exists(path + ".migration-2.bak"));
            await repository.SetChannelOrderAsync(["channel"], cancellationToken);
            Assert.Equal(0, Assert.Single(await repository.GetChannelsAsync(cancellationToken)).SortOrder);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GuideTimingTransactionRollsBackDefinitionIfPendingInstanceUpdateFails()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fritztv-guide-timer-atomic-tests-" + Guid.NewGuid().ToString("N"));
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.Combine(directory, "app.db");
        try
        {
            var repository = new AppRepository(path);
            await repository.InitializeAsync(cancellationToken);
            var start = new DateTimeOffset(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
            var definition = new RecordingDefinition(Guid.NewGuid(), "channel", "Test", start, start.AddHours(1), "Europe/Berlin");
            await repository.SaveDefinitionAsync(definition, cancellationToken);
            var pending = new RecordingInstance(Guid.NewGuid(), definition.Id, definition.ChannelId, definition.Title, start.AddMinutes(-2), start.AddHours(1).AddMinutes(5), start, RecordingState.Scheduled);
            await repository.SaveRecordingAsync(pending, cancellationToken);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                await connection.OpenAsync(cancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TRIGGER fail_pending BEFORE UPDATE ON recordings BEGIN SELECT RAISE(ABORT,'Synthetic storage failure'); END;";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await Assert.ThrowsAsync<SqliteException>(() => repository.UpdateDefinitionTimingAsync(definition with { Start = start.AddMinutes(15), End = start.AddMinutes(75) }, cancellationToken));
            Assert.Equal(definition, Assert.Single(await repository.GetDefinitionsAsync(cancellationToken)));
            Assert.Equal(pending, Assert.Single(await repository.GetRecordingsAsync(cancellationToken)));
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
