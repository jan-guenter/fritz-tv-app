using System.Buffers.Binary;
using FritzTV.Core.Models;
using FritzTV.Infrastructure.Dvb;
using FritzTV.Infrastructure.Persistence;
using FritzTV.Infrastructure.Playback;
using FritzTV.Infrastructure.Recording;
using FritzTV.Infrastructure.Transport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class RecordingAliasTests
{
    [Fact]
    public async Task GuidePromotionAfterPlaybackReadinessKeepsRetainedRecordingAlive()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "fritztv-recording-alias-" + Guid.NewGuid().ToString("N"));
        try
        {
            await VerifyPromotionAsync(directory, cancellationToken);
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

    private static async Task VerifyPromotionAsync(string directory, CancellationToken cancellationToken)
    {
        var repository = new AppRepository(Path.Combine(directory, "app.db"));
        await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var server = new LoopbackRtspServer();
        await using var serverScope = server.ConfigureAwait(false);
        var manager = new SatIpSessionManager(32656, 32783);
        await using var managerScope = manager.ConfigureAwait(false);
        var device = new SatIpDevice(Guid.NewGuid(), "uuid:synthetic", "Synthetic", new Uri($"http://127.0.0.1:{server.Port}/device.xml"), null, null, 1);
        var channel = new TvChannel("provisional-channel", device.Id, "Synthetic radio", ChannelKind.Radio, new Uri($"rtsp://127.0.0.1:{server.Port}/?freq=444&pids=0,99,100,200"), 42);
        await repository.SaveDeviceAsync(device, cancellationToken).ConfigureAwait(false);
        await repository.SaveChannelsAsync([channel], cancellationToken).ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var starting = BufferedPlayback.StartAsync(manager, device, channel, new AppSettings { BufferDirectory = Path.Combine(directory, "buffer"), FreeSpaceReserveBytes = 0 }, repository, lifetime.Token);
        await FeedStartupAsync(server, starting, cancellationToken).ConfigureAwait(false);
        var playback = await starting.ConfigureAwait(false);
        await using var playbackScope = playback.ConfigureAwait(false);
        Assert.Equal(channel.Id, playback.Channel.Id);
        var promoted = await repository.ResolveChannelAsync(channel.Id, new DvbServiceIdentity(device.Id, 1, 1101, 42), cancellationToken).ConfigureAwait(false);
        Assert.NotEqual(channel.Id, promoted.Id, StringComparer.Ordinal);
        Assert.True(await repository.AreSameChannelAsync(playback.Channel, promoted, cancellationToken).ConfigureAwait(false));
        var factory = new SatIpRecordingInputFactory(repository, manager);
        var input = await factory.OpenAsync(promoted, playback, playback.Buffer.Range!.Earliest, cancellationToken).ConfigureAwait(false);
        await using var inputScope = input.ConfigureAwait(false);
        await playback.DisposeAsync().ConfigureAwait(false);
        Assert.True(playback.IsRunning);
        var reader = input.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using var readerScope = reader.ConfigureAwait(false);
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false));
        var sections = new List<PsiSectionEventArgs>();
        var assembler = new PsiSectionAssembler();
        assembler.SectionReceived += (_, section) => sections.Add(section);
        for (var offset = 0; offset + 188 <= reader.Current.Length; offset += 188)
        {
            assembler.Push(reader.Current.Span.Slice(offset, 188));
        }

        Assert.Contains(sections, section => ProgrammeMapParser.ParsePmt(section.Pid, section.Data.Span)?.ServiceId == 42);
        Assert.Equal(42, input.ServiceId);
    }

    private static async Task FeedStartupAsync(LoopbackRtspServer server, Task<BufferedPlayback> starting, CancellationToken cancellationToken)
    {
        byte[] pmt = [2, 0xb0, 18, 0, 42, 0xc1, 0, 0, 0xe0, 200, 0xf0, 0, 0x11, 0xe0, 100, 0xf0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt32BigEndian(pmt.AsSpan(pmt.Length - 4), MpegCrc32.Compute(pmt.AsSpan(0, pmt.Length - 4)));
        var audio = new byte[188];
        audio[0] = 0x47;
        audio[1] = 0x40;
        audio[2] = 100;
        audio[3] = 0x10;
        audio[6] = 1;
        audio[7] = 0xc0;
        var initial = PsiPacketizer.Packetize(0, DvbFixtures.PatService(42, 99, 0, 0)).Concat(PsiPacketizer.Packetize(99, pmt)).Concat(audio).ToArray();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        ushort sequence = 1;
        while (!starting.IsCompleted)
        {
            await server.SendBatchAsync(initial, sequence++, deadline.Token).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token).ConfigureAwait(false);
        }
    }
}
