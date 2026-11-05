using System.Buffers.Binary;
using FritzTV.Core.Models;
using FritzTV.Infrastructure.Dvb;
using FritzTV.Infrastructure.Playback;
using FritzTV.Infrastructure.Transport;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class BufferedPlaybackTests
{
    [Fact]
    public async Task PatMoveKeepsReceptionUntilNewPmtReplacesTheElementaryPidSet()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "fritztv-buffer-pid-move-" + Guid.NewGuid().ToString("N"));
        await using var server = new LoopbackRtspServer();
        await using var manager = new SatIpSessionManager(32000, 32127);
        var device = new SatIpDevice(Guid.NewGuid(), "uuid:synthetic", "Synthetic", new Uri($"http://127.0.0.1:{server.Port}/device.xml"), null, null, 1);
        var channel = new TvChannel("channel", device.Id, "Synthetic radio", ChannelKind.Radio, new Uri($"rtsp://127.0.0.1:{server.Port}/?freq=444&pids=0,99,100,200"), 42);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var starting = BufferedPlayback.StartAsync(manager, device, channel, new AppSettings { BufferDirectory = directory, FreeSpaceReserveBytes = 0 }, lifetime.Token);
        try
        {
            var sequence = await FeedStartupAsync(server, starting, cancellationToken);
            await using var playback = await starting;
            Assert.Equal(99, playback.Programme!.PmtPid);
            await server.SendBatchAsync(PsiPacketizer.Packetize(0, DvbFixtures.PatService(42, 98, 0, 0, version: 1), 1), sequence++, cancellationToken);
            await WaitUntilAsync(() => server.Requests.Any(request => request.Contains("addpids=98", StringComparison.Ordinal)), cancellationToken);
            await server.SendBatchAsync(PsiPacketizer.Packetize(98, Pmt(101)), sequence, cancellationToken);
            await WaitUntilAsync(() => playback.Programme?.PmtPid == 98 && server.Requests.Any(request => request.Contains("delpids=99,100", StringComparison.Ordinal)), cancellationToken);
            Assert.Equal(101, Assert.Single(playback.Programme!.Streams).Pid);
            Assert.Null(playback.Failure);
        }
        finally
        {
            await lifetime.CancelAsync();
            try
            {
                await using var cleanup = await starting;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task<ushort> FeedStartupAsync(LoopbackRtspServer server, Task<BufferedPlayback> starting, CancellationToken cancellationToken)
    {
        var initial = PsiPacketizer.Packetize(0, DvbFixtures.PatService(42, 99, 0, 0)).Concat(PsiPacketizer.Packetize(99, Pmt(100))).Concat(AudioPacket()).ToArray();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        ushort sequence = 1;
        while (!starting.IsCompleted)
        {
            await server.SendBatchAsync(initial, sequence++, deadline.Token).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token).ConfigureAwait(false);
        }
        return sequence;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token).ConfigureAwait(false);
        }
    }

    private static byte[] Pmt(int audioPid)
    {
        byte[] body = [2, 0xb0, 18, 0, 42, 0xc1, 0, 0, 0xe0, 200, 0xf0, 0, 0x11, 0xe0, (byte)audioPid, 0xf0, 0];
        var output = new byte[body.Length + 4];
        body.CopyTo(output, 0);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(body.Length), MpegCrc32.Compute(body));
        return output;
    }

    private static byte[] AudioPacket()
    {
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x40;
        packet[2] = 100;
        packet[3] = 0x10;
        packet[6] = 1;
        packet[7] = 0xc0;
        return packet;
    }
}
