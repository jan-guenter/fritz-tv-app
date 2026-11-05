using System.Globalization;
using System.Text.Json;
using FritzTV.Core.Models;
using Microsoft.Data.Sqlite;
using FritzTV.Infrastructure.Transport;

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
        if (version > 2)
        {
            throw new InvalidDataException("The database was created by a newer application version.");
        }

        if (version == 2)
        {
            return;
        }

        if (existed)
        {
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fullPath + ".migration-" + version.ToString(CultureInfo.InvariantCulture) + ".bak" }.ToString());
            await backup.OpenAsync(cancellationToken).ConfigureAwait(false);
            connection.BackupDatabase(backup);
        }

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS channel_aliases (alias TEXT PRIMARY KEY, channel_id TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS devices (id TEXT PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS channels (id TEXT PRIMARY KEY, device_id TEXT NOT NULL, favourite INTEGER NOT NULL DEFAULT 0, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS programmes (id TEXT PRIMARY KEY, channel_key TEXT NOT NULL, start INTEGER NOT NULL, end INTEGER NOT NULL, payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_programmes_range ON programmes(channel_key, start, end);
            CREATE TABLE IF NOT EXISTS definitions (id TEXT PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS recordings (id TEXT PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS settings (id TEXT PRIMARY KEY, payload TEXT NOT NULL);
            PRAGMA user_version=2;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task SaveDeviceAsync(SatIpDevice device, CancellationToken cancellationToken = default) => SaveAsync("devices", device.Id.ToString("D", CultureInfo.InvariantCulture), device, cancellationToken);

    public Task<IReadOnlyList<SatIpDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) => ReadAllAsync<SatIpDevice>("devices", cancellationToken);

    public Task SaveChannelsAsync(IEnumerable<TvChannel> channels, CancellationToken cancellationToken = default) => WriteChannelsAsync(null, channels, cancellationToken);

    public Task ReplaceDeviceChannelsAsync(Guid deviceId, IEnumerable<TvChannel> channels, CancellationToken cancellationToken = default) => WriteChannelsAsync(deviceId, channels, cancellationToken);

    private async Task WriteChannelsAsync(Guid? replaceDevice, IEnumerable<TvChannel> channels, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var incoming = channels.ToArray();
        if (replaceDevice is { } device && incoming.Any(channel => channel.DeviceId != device))
        {
            throw new ArgumentException("All refreshed channels must belong to the selected device.", nameof(channels));
        }

        var previous = await GetChannelsAsync(cancellationToken).ConfigureAwait(false);
        var byTuneAndName = previous.GroupBy(ChannelRefreshKey, StringComparer.Ordinal).Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var channel in incoming)
        {
            current.Add(await WriteChannelAsync(connection, transaction, channel, byTuneAndName.GetValueOrDefault(ChannelRefreshKey(channel)), cancellationToken).ConfigureAwait(false));
        }

        if (replaceDevice is { } deviceId)
        {
            await ArchiveMissingChannelsAsync(connection, transaction, deviceId, current, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> WriteChannelAsync(SqliteConnection connection, SqliteTransaction transaction, TvChannel channel, TvChannel? prior, CancellationToken cancellationToken)
    {
        var canonicalId = await ResolveIdAsync(connection, transaction, channel.Id, cancellationToken).ConfigureAwait(false);
        if (string.Equals(canonicalId, channel.Id, StringComparison.Ordinal) && prior is not null)
        {
            canonicalId = await ResolveIdAsync(connection, transaction, prior.Id, cancellationToken).ConfigureAwait(false);
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload FROM channels WHERE id=$id;";
        command.Parameters.AddWithValue("$id", canonicalId);
        var oldPayload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        var existing = oldPayload is null ? null : JsonSerializer.Deserialize<TvChannel>(oldPayload, JsonOptions);
        var updated = channel with { Id = canonicalId, ServiceId = channel.ServiceId ?? existing?.ServiceId, ServiceIdentity = channel.ServiceIdentity ?? existing?.ServiceIdentity };
        command.CommandText = "INSERT INTO channels(id,device_id,favourite,payload) VALUES($id,$device,$favourite,$payload) ON CONFLICT(id) DO UPDATE SET device_id=excluded.device_id,payload=excluded.payload; INSERT INTO channel_aliases(alias,channel_id) VALUES($alias,$id) ON CONFLICT(alias) DO UPDATE SET channel_id=excluded.channel_id;";
        command.Parameters.AddWithValue("$alias", channel.Id);
        command.Parameters.AddWithValue("$device", updated.DeviceId.ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$favourite", updated.IsFavorite);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(updated, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return updated.Id;
    }

    private static async Task ArchiveMissingChannelsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid deviceId, HashSet<string> current, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM channels WHERE device_id=$device;";
        command.Parameters.AddWithValue("$device", deviceId.ToString("D", CultureInfo.InvariantCulture));
        var missing = new List<string>();
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetString(0);
                if (!current.Contains(id))
                {
                    missing.Add(id);
                }
            }
        }

        command.CommandText = "UPDATE channels SET payload=json_set(payload,'$.IsAvailable',json('false')) WHERE id=$id;";
        var idParameter = command.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in missing)
        {
            idParameter.Value = id;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<TvChannel>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload,favourite FROM channels WHERE COALESCE(json_extract(payload,'$.IsAvailable'),1)=1 ORDER BY rowid;";
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
        command.CommandText = "UPDATE channels SET favourite=$favourite WHERE id=COALESCE((SELECT channel_id FROM channel_aliases WHERE alias=$id),$id);";
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

    public async Task PruneProgrammesAsync(DateTimeOffset oldest, DateTimeOffset newest, CancellationToken cancellationToken = default)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM programmes WHERE end<$oldest OR start>$newest;";
        command.Parameters.AddWithValue("$oldest", oldest.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$newest", newest.ToUnixTimeSeconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveDefinitionAsync(RecordingDefinition definition, CancellationToken cancellationToken = default)
    {
        var channel = await GetChannelAsync(definition.ChannelId, cancellationToken).ConfigureAwait(false);
        var updated = channel is null ? definition : definition with { ChannelId = channel.Id };
        await SaveAsync("definitions", definition.Id.ToString("D", CultureInfo.InvariantCulture), updated, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<RecordingDefinition>> GetDefinitionsAsync(CancellationToken cancellationToken = default) => ReadAllAsync<RecordingDefinition>("definitions", cancellationToken);

    public async Task SaveRecordingAsync(RecordingInstance recording, CancellationToken cancellationToken = default)
    {
        var channel = await GetChannelAsync(recording.ChannelId, cancellationToken).ConfigureAwait(false);
        var updated = channel is null ? recording : recording with { ChannelId = channel.Id };
        await SaveAsync("recordings", recording.Id.ToString("D", CultureInfo.InvariantCulture), updated, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<RecordingInstance>> GetRecordingsAsync(CancellationToken cancellationToken = default) => ReadAllAsync<RecordingInstance>("recordings", cancellationToken);

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => SaveAsync("settings", "app", settings, cancellationToken);

    public async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await ReadAllAsync<AppSettings>("settings", cancellationToken).ConfigureAwait(false);
        return settings.Count > 0 ? settings[0] : new AppSettings();
    }

    public static string ServiceKey(DvbServiceIdentity identity) => string.Create(CultureInfo.InvariantCulture, $"{identity.DeviceId:D}:{identity.OriginalNetworkId}:{identity.TransportStreamId}:{identity.ServiceId}");

    public Task DeleteDefinitionAsync(Guid id, CancellationToken cancellationToken = default) => DeleteAsync("definitions", id, cancellationToken);

    public Task DeleteRecordingAsync(Guid id, CancellationToken cancellationToken = default) => DeleteAsync("recordings", id, cancellationToken);

    private async Task DeleteAsync(string table, Guid id, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {table} WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TvChannel?> GetChannelAsync(string id, CancellationToken cancellationToken = default)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload,favourite FROM channels WHERE id=COALESCE((SELECT channel_id FROM channel_aliases WHERE alias=$id),$id);";
        command.Parameters.AddWithValue("$id", id);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var channel = JsonSerializer.Deserialize<TvChannel>(reader.GetString(0), JsonOptions) ?? throw new InvalidDataException("Invalid stored channel.");
        return channel with { IsFavorite = reader.GetBoolean(1) };
    }

    public async Task<TvChannel> ResolveChannelAsync(string id, DvbServiceIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var existing = await GetChannelAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("The channel is not stored.");
        if (existing.DeviceId != identity.DeviceId)
        {
            throw new ArgumentException("The DVB identity belongs to a different device.", nameof(identity));
        }

        var resolved = existing with { Id = ChannelIdentity.ForService(identity), ServiceId = identity.ServiceId, ServiceIdentity = identity };
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$old", existing.Id);
        command.Parameters.AddWithValue("$new", resolved.Id);
        command.Parameters.AddWithValue("$alias", id);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(resolved, JsonOptions));
        if (!string.Equals(existing.Id, resolved.Id, StringComparison.Ordinal))
        {
            command.CommandText = "SELECT COUNT(*) FROM channels WHERE id=$new;";
            var alreadyPresent = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
            command.CommandText = alreadyPresent
                ? "UPDATE channels SET favourite=MAX(favourite,(SELECT favourite FROM channels WHERE id=$old)),payload=$payload WHERE id=$new; DELETE FROM channels WHERE id=$old;"
                : "UPDATE channels SET id=$new,payload=$payload WHERE id=$old;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            command.CommandText = "UPDATE channels SET payload=$payload WHERE id=$new;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        command.CommandText = """
            INSERT INTO channel_aliases(alias,channel_id) VALUES($alias,$new) ON CONFLICT(alias) DO UPDATE SET channel_id=excluded.channel_id;
            INSERT INTO channel_aliases(alias,channel_id) VALUES($old,$new) ON CONFLICT(alias) DO UPDATE SET channel_id=excluded.channel_id;
            UPDATE channel_aliases SET channel_id=$new WHERE channel_id=$old;
            UPDATE definitions SET payload=json_set(payload,'$.ChannelId',$new) WHERE json_extract(payload,'$.ChannelId')=$old;
            UPDATE recordings SET payload=json_set(payload,'$.ChannelId',$new) WHERE json_extract(payload,'$.ChannelId')=$old;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return resolved;
    }

    private static string ChannelRefreshKey(TvChannel channel) => string.Create(CultureInfo.InvariantCulture, $"{channel.DeviceId:D}|{SatIpTuning.Key(channel.StreamUri)}|{channel.Name}");

    private static async Task<string> ResolveIdAsync(SqliteConnection connection, SqliteTransaction transaction, string id, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE((SELECT channel_id FROM channel_aliases WHERE alias=$id),$id);";
        command.Parameters.AddWithValue("$id", id);
        return (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? id);
    }

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
