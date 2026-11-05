using FritzTV.Core.Models;
using FritzTV.Infrastructure.Dvb;
using FritzTV.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class ChannelIdentityTests
{
    [Fact]
    public void PatAndSdtResolveServiceWithoutGuessingFromChannelName()
    {
        var device = Guid.NewGuid();
        var resolver = new DvbChannelResolver(device);
        resolver.Observe(new PsiSectionEventArgs(0, DvbFixtures.Pat()));
        resolver.Observe(new PsiSectionEventArgs(17, DvbFixtures.Sdt()));
        var channel = new TvChannel("provisional", device, "Unrelated user label", ChannelKind.Television, new Uri("rtsp://192.0.2.1/?freq=386&pids=0,16,17,18,20,5100,5101"));
        Assert.Equal(new DvbServiceIdentity(device, 1, 1101, 10301), resolver.Resolve(channel));
        var transport = SdtParser.Parse(DvbFixtures.Sdt());
        Assert.Equal("Synthetic service", Assert.Single(transport!.Services).Name);
        Assert.Null(resolver.Resolve(channel with { StreamUri = new Uri("rtsp://192.0.2.1/?freq=386&pids=0,18,7777") }));
    }

    [Fact]
    public void MultiSectionPatAndSdtRetainServicesUntilANewerVersionReplacesThem()
    {
        var device = Guid.NewGuid();
        var resolver = new DvbChannelResolver(device);
        resolver.Observe(new PsiSectionEventArgs(0, DvbFixtures.PatService(10301, 5100, 0, 1)));
        resolver.Observe(new PsiSectionEventArgs(0, DvbFixtures.PatService(11110, 6100, 1, 1)));
        resolver.Observe(new PsiSectionEventArgs(17, DvbFixtures.SdtService(11110, 1, 1)));
        resolver.Observe(new PsiSectionEventArgs(17, DvbFixtures.SdtService(10301, 0, 1)));
        var first = new TvChannel("first", device, "First", ChannelKind.Television, new Uri("rtsp://192.0.2.1/?freq=386&pids=0,5100,5101"));
        var second = first with { Id = "second", StreamUri = new Uri("rtsp://192.0.2.1/?freq=386&pids=0,6100,6101") };
        Assert.Equal((ushort)10301, resolver.Resolve(first)!.ServiceId);
        Assert.Equal((ushort)11110, resolver.Resolve(second)!.ServiceId);
        resolver.Observe(new PsiSectionEventArgs(0, DvbFixtures.PatService(10301, 5200, 0, 0, version: 1)));
        resolver.Observe(new PsiSectionEventArgs(0, DvbFixtures.PatService(11110, 6100, 1, 1)));
        Assert.Null(resolver.Resolve(first));
        Assert.Null(resolver.Resolve(second));
        Assert.Equal((ushort)10301, resolver.Resolve(first with { StreamUri = new Uri("rtsp://192.0.2.1/?freq=386&pids=0,5200,5201") })!.ServiceId);
    }

    [Fact]
    public async Task ResolvingDvbIdentityMigratesTimerReferencesAndKeepsAliases()
    {
        await WithRepositoryAsync(async repository =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var channel = new TvChannel("provisional", Guid.NewGuid(), "Test", ChannelKind.Television, new Uri("rtsp://192.0.2.1/?freq=386&pids=0,18,5100,5101"));
            await repository.SaveChannelsAsync([channel], cancellationToken).ConfigureAwait(false);
            await repository.SetFavoriteAsync(channel.Id, true, cancellationToken).ConfigureAwait(false);
            var start = new DateTimeOffset(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
            var timer = new RecordingDefinition(Guid.NewGuid(), channel.Id, "Test", start, start.AddHours(1), "Europe/Berlin");
            await repository.SaveDefinitionAsync(timer, cancellationToken).ConfigureAwait(false);
            var identity = new DvbServiceIdentity(channel.DeviceId, 1, 1101, 10301);
            var resolved = await repository.ResolveChannelAsync(channel.Id, identity, cancellationToken).ConfigureAwait(false);
            Assert.Equal(ChannelIdentity.ForService(identity), resolved.Id, StringComparer.Ordinal);
            Assert.True((await repository.GetChannelAsync(channel.Id, cancellationToken).ConfigureAwait(false))!.IsFavorite);
            Assert.Equal(resolved.Id, Assert.Single(await repository.GetDefinitionsAsync(cancellationToken).ConfigureAwait(false)).ChannelId, StringComparer.Ordinal);
            await repository.ReplaceDeviceChannelsAsync(channel.DeviceId, [channel with { StreamUri = new Uri("rtsp://192.0.2.1/?freq=386&pids=0,18,5100,5102") }], cancellationToken).ConfigureAwait(false);
            var refreshed = Assert.Single(await repository.GetChannelsAsync(cancellationToken).ConfigureAwait(false));
            Assert.Equal(identity, refreshed.ServiceIdentity);
            Assert.True(refreshed.IsFavorite);
            Assert.Equal(resolved.Id, refreshed.Id, StringComparer.Ordinal);
        });
    }

    [Fact]
    public async Task RemovedStationsAreArchivedAndReturningStationsRecoverPreferences()
    {
        await WithRepositoryAsync(async repository =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var channel = new TvChannel("channel", Guid.NewGuid(), "Test", ChannelKind.Radio, new Uri("rtsp://192.0.2.1/?freq=386&pids=0,18,100"));
            await repository.ReplaceDeviceChannelsAsync(channel.DeviceId, [channel], cancellationToken).ConfigureAwait(false);
            await repository.SetFavoriteAsync(channel.Id, true, cancellationToken).ConfigureAwait(false);
            await repository.ReplaceDeviceChannelsAsync(channel.DeviceId, [], cancellationToken).ConfigureAwait(false);
            Assert.Empty(await repository.GetChannelsAsync(cancellationToken).ConfigureAwait(false));
            Assert.False((await repository.GetChannelAsync(channel.Id, cancellationToken).ConfigureAwait(false))!.IsAvailable);
            await repository.ReplaceDeviceChannelsAsync(channel.DeviceId, [channel], cancellationToken).ConfigureAwait(false);
            Assert.True(Assert.Single(await repository.GetChannelsAsync(cancellationToken).ConfigureAwait(false)).IsFavorite);
        });
    }

    [Fact]
    public async Task ConcurrentLiveAndGuideResolutionPreservesMergedFavoritesAndAliases()
    {
        await WithRepositoryAsync(async repository =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var channel = new TvChannel("first-alias", Guid.NewGuid(), "Test", ChannelKind.Television, new Uri("rtsp://192.0.2.1/?freq=386&pids=0,5100,5101"));
            var second = channel with { Id = "second-alias" };
            await repository.SaveChannelsAsync([channel, second], cancellationToken).ConfigureAwait(false);
            await repository.SetFavoriteAsync(second.Id, true, cancellationToken).ConfigureAwait(false);
            var identity = new DvbServiceIdentity(channel.DeviceId, 1, 1101, 10301);
            await repository.ResolveChannelAsync(second.Id, identity, cancellationToken).ConfigureAwait(false);
            var resolved = await repository.ResolveChannelAsync(channel.Id, identity, cancellationToken).ConfigureAwait(false);
            Assert.True(resolved.IsFavorite);
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () => await repository.ResolveChannelAsync(channel.Id, identity, cancellationToken).ConfigureAwait(false), cancellationToken));
            await Task.WhenAll(tasks).ConfigureAwait(false);
            var saved = Assert.Single(await repository.GetChannelsAsync(cancellationToken).ConfigureAwait(false));
            Assert.True(saved.IsFavorite);
            Assert.Equal(saved.Id, (await repository.GetChannelAsync(second.Id, cancellationToken).ConfigureAwait(false))!.Id, StringComparer.Ordinal);
        });
    }

    [Fact]
    public async Task GlobalChannelOrderSurvivesAliasPromotionAndPlaylistRefresh()
    {
        await WithRepositoryAsync(async repository =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var first = new TvChannel("first", Guid.NewGuid(), "First", ChannelKind.Television, new Uri("rtsp://192.0.2.1/?freq=386&pids=0,5100,5101"));
            var second = first with { Id = "second", Name = "Second", StreamUri = new Uri("rtsp://192.0.2.1/?freq=450&pids=0,6100,6101") };
            await repository.SaveChannelsAsync([first, second], cancellationToken).ConfigureAwait(false);
            await repository.SetChannelOrderAsync([second.Id, first.Id], cancellationToken).ConfigureAwait(false);
            var promoted = await repository.ResolveChannelAsync(first.Id, new DvbServiceIdentity(first.DeviceId, 1, 1101, 10301), cancellationToken).ConfigureAwait(false);
            await repository.ReplaceDeviceChannelsAsync(first.DeviceId, [first with { StreamUri = new Uri("rtsp://192.0.2.1/?freq=386&pids=0,5100,5102") }, second], cancellationToken).ConfigureAwait(false);
            Assert.Equal(new[] { second.Id, promoted.Id }, (await repository.GetChannelsAsync(cancellationToken).ConfigureAwait(false)).Select(channel => channel.Id), StringComparer.Ordinal);
            await repository.SetChannelOrderAsync([first.Id, second.Id], cancellationToken).ConfigureAwait(false);
            Assert.Equal(new[] { promoted.Id, second.Id }, (await repository.GetChannelsAsync(cancellationToken).ConfigureAwait(false)).Select(channel => channel.Id), StringComparer.Ordinal);
        });
    }

    private static async Task WithRepositoryAsync(Func<AppRepository, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "fritztv-identity-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new AppRepository(Path.Combine(directory, "app.db"));
            await repository.InitializeAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            await action(repository).ConfigureAwait(false);
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
