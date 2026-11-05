using System.Globalization;
using System.Text.Json;
using FritzTV.Core.Models;
using Microsoft.Data.Sqlite;

namespace FritzTV.Infrastructure.Persistence;

public sealed class AppRepository(string databasePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly string fullPath = Path.GetFullPath(databasePath);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var existed = File.Exists(fullPath) && new FileInfo(fullPath).Length > 0;
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (version > 1)
        {
            throw new InvalidDataException("The database was created by a newer application version.");
        }

        if (version == 1)
        {
            return;
        }

        if (existed)
        {
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fullPath + ".migration-0.bak" }.ToString());
            await backup.OpenAsync(cancellationToken).ConfigureAwait(false);
            connection.BackupDatabase(backup);
        }

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS devices (id TEXT PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS channels (id TEXT PRIMARY KEY, device_id TEXT NOT NULL, favourite INTEGER NOT NULL DEFAULT 0, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS programmes (id TEXT PRIMARY KEY, channel_key TEXT NOT NULL, start INTEGER NOT NULL, end INTEGER NOT NULL, payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_programmes_range ON programmes(channel_key, start, end);
            CREATE TABLE IF NOT EXISTS definitions (id TEXT PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS recordings (id TEXT PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS settings (id TEXT PRIMARY KEY, payload TEXT NOT NULL);
            PRAGMA user_version=1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task SaveDeviceAsync(SatIpDevice device, CancellationToken cancellationToken = default) => SaveAsync("devices", device.Id.ToString("D", CultureInfo.InvariantCulture), device, cancellationToken);

    public Task<IReadOnlyList<SatIpDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) => ReadAllAsync<SatIpDevice>("devices", cancellationToken);

    public async Task SaveChannelsAsync(IEnumerable<TvChannel> channels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        foreach (var channel in channels)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO channels(id,device_id,favourite,payload) VALUES($id,$device,$favourite,$payload) ON CONFLICT(id) DO UPDATE SET device_id=excluded.device_id,payload=excluded.payload;";
            command.Parameters.AddWithValue("$id", channel.Id);
            command.Parameters.AddWithValue("$device", channel.DeviceId.ToString("D", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$favourite", channel.IsFavorite);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(channel, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TvChannel>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload,favourite FROM channels ORDER BY rowid;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<TvChannel>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var channel = JsonSerializer.Deserialize<TvChannel>(reader.GetString(0), JsonOptions) ?? throw new InvalidDataException("Invalid stored channel.");
            result.Add(channel with { IsFavorite = reader.GetBoolean(1) });
        }

        return result;
    }

    public async Task SetFavoriteAsync(string channelId, bool favorite, CancellationToken cancellationToken = default)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE channels SET favourite=$favourite WHERE id=$id;";
        command.Parameters.AddWithValue("$id", channelId);
        command.Parameters.AddWithValue("$favourite", favorite);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveProgrammesAsync(IEnumerable<Programme> programmes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(programmes);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        foreach (var programme in programmes)
        {
            var serviceKey = ServiceKey(programme.Service);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO programmes(id,channel_key,start,end,payload) VALUES($id,$channel,$start,$end,$payload) ON CONFLICT(id) DO UPDATE SET start=excluded.start,end=excluded.end,payload=excluded.payload;";
            command.Parameters.AddWithValue("$id", serviceKey + ":" + programme.EventId.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$channel", serviceKey);
            command.Parameters.AddWithValue("$start", programme.Start.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$end", programme.End.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(programme, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Programme>> GetProgrammesAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken = default)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM programmes WHERE start<$until AND end>$from ORDER BY start;";
        command.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$until", until.ToUnixTimeSeconds());
        return await ReadAsync<Programme>(command, cancellationToken).ConfigureAwait(false);
    }

    public Task SaveDefinitionAsync(RecordingDefinition definition, CancellationToken cancellationToken = default) => SaveAsync("definitions", definition.Id.ToString("D", CultureInfo.InvariantCulture), definition, cancellationToken);

    public Task<IReadOnlyList<RecordingDefinition>> GetDefinitionsAsync(CancellationToken cancellationToken = default) => ReadAllAsync<RecordingDefinition>("definitions", cancellationToken);

    public Task SaveRecordingAsync(RecordingInstance recording, CancellationToken cancellationToken = default) => SaveAsync("recordings", recording.Id.ToString("D", CultureInfo.InvariantCulture), recording, cancellationToken);

    public Task<IReadOnlyList<RecordingInstance>> GetRecordingsAsync(CancellationToken cancellationToken = default) => ReadAllAsync<RecordingInstance>("recordings", cancellationToken);

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => SaveAsync("settings", "app", settings, cancellationToken);

    public async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await ReadAllAsync<AppSettings>("settings", cancellationToken).ConfigureAwait(false);
        return settings.Count > 0 ? settings[0] : new AppSettings();
    }

    public static string ServiceKey(DvbServiceIdentity identity) => string.Create(CultureInfo.InvariantCulture, $"{identity.DeviceId:D}:{identity.OriginalNetworkId}:{identity.TransportStreamId}:{identity.ServiceId}");

    private async Task SaveAsync<T>(string table, string id, T value, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO {table}(id,payload) VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<T>> ReadAllAsync<T>(string table, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload FROM {table} ORDER BY rowid;";
        return await ReadAsync<T>(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<T>> ReadAsync<T>(SqliteCommand command, CancellationToken cancellationToken)
    {
        var result = new List<T>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), JsonOptions) ?? throw new InvalidDataException("Invalid stored data."));
        }

        return result;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fullPath, ForeignKeys = true, DefaultTimeout = 5 }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
