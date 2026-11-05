using FritzTV.Core.Models;

namespace FritzTV.Desktop.Services;

public interface IDesktopData
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken);

    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken);

    Task<IReadOnlyList<SatIpDevice>> GetDevicesAsync(CancellationToken cancellationToken);

    Task SaveDeviceAsync(SatIpDevice device, CancellationToken cancellationToken);

    Task<IReadOnlyList<TvChannel>> GetChannelsAsync(CancellationToken cancellationToken);

    Task<TvChannel?> GetChannelAsync(string id, CancellationToken cancellationToken);

    Task ReplaceDeviceChannelsAsync(Guid deviceId, IEnumerable<TvChannel> channels, CancellationToken cancellationToken);

    Task SetFavoriteAsync(string id, bool favorite, CancellationToken cancellationToken);

    Task SetChannelOrderAsync(IEnumerable<string> orderedIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<RecordingDefinition>> GetDefinitionsAsync(CancellationToken cancellationToken);

    Task SaveProgrammesAsync(IEnumerable<Programme> programmes, CancellationToken cancellationToken);

    Task<IReadOnlyList<Programme>> GetProgrammesAsync(DvbServiceIdentity service, DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken);
}
