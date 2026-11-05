using FritzTV.Core.Models;
using FritzTV.Infrastructure.Persistence;

namespace FritzTV.Desktop.Services;

public sealed class DesktopDataService(AppRepository repository) : IDesktopData
{
    public Task InitializeAsync(CancellationToken cancellationToken) => repository.InitializeAsync(cancellationToken);

    public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken) => repository.GetSettingsAsync(cancellationToken);

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken) => repository.SaveSettingsAsync(settings, cancellationToken);

    public Task<IReadOnlyList<SatIpDevice>> GetDevicesAsync(CancellationToken cancellationToken) => repository.GetDevicesAsync(cancellationToken);

    public Task SaveDeviceAsync(SatIpDevice device, CancellationToken cancellationToken) => repository.SaveDeviceAsync(device, cancellationToken);

    public Task<IReadOnlyList<TvChannel>> GetChannelsAsync(CancellationToken cancellationToken) => repository.GetChannelsAsync(cancellationToken);

    public Task<TvChannel?> GetChannelAsync(string id, CancellationToken cancellationToken) => repository.GetChannelAsync(id, cancellationToken);

    public Task ReplaceDeviceChannelsAsync(Guid deviceId, IEnumerable<TvChannel> channels, CancellationToken cancellationToken) => repository.ReplaceDeviceChannelsAsync(deviceId, channels, cancellationToken);

    public Task SetFavoriteAsync(string id, bool favorite, CancellationToken cancellationToken) => repository.SetFavoriteAsync(id, favorite, cancellationToken);

    public Task SetChannelOrderAsync(IEnumerable<string> orderedIds, CancellationToken cancellationToken) => repository.SetChannelOrderAsync(orderedIds, cancellationToken);

    public Task<IReadOnlyList<RecordingDefinition>> GetDefinitionsAsync(CancellationToken cancellationToken) => repository.GetDefinitionsAsync(cancellationToken);

    public Task SaveProgrammesAsync(IEnumerable<Programme> programmes, CancellationToken cancellationToken) => repository.SaveProgrammesAsync(programmes, cancellationToken);

    public Task<IReadOnlyList<Programme>> GetProgrammesAsync(DvbServiceIdentity service, DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken) => repository.GetProgrammesAsync(service, from, until, cancellationToken);
}
