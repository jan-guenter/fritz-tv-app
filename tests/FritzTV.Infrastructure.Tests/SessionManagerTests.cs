using FritzTV.Core.Exceptions;
using FritzTV.Core.Models;
using FritzTV.Infrastructure.Transport;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class SessionManagerTests
{
    [Fact]
    public async Task SharedFrequencyUnionsAndRemovesPidsWithoutAllocatingAnotherTuner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = new LoopbackRtspServer();
        await using var manager = new SatIpSessionManager(31000, 31127);
        var device = Device(server.Port, 1);
        await using var first = await manager.AcquireAsync(device, Channel(device, server.Port, 444, 100), TunerPriority.Viewing, cancellationToken);
        var second = await manager.AcquireAsync(device, Channel(device, server.Port, 444, 200), TunerPriority.Recording, cancellationToken);
        Assert.Same(first.Session, second.Session);
        Assert.Single(server.Requests, value => value.StartsWith("SETUP ", StringComparison.Ordinal));
        Assert.Contains(server.Requests, value => value.Contains("addpids=200", StringComparison.Ordinal));
        await second.DisposeAsync();
        Assert.Contains(server.Requests, value => value.Contains("delpids=200", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecordingPreemptsViewingButCannotPreemptAnotherRecording()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = new LoopbackRtspServer();
        await using var manager = new SatIpSessionManager(31200, 31327);
        var device = Device(server.Port, 1);
        await using var viewing = await manager.AcquireAsync(device, Channel(device, server.Port, 444, 100), TunerPriority.Viewing, cancellationToken);
        await using var recording = await manager.AcquireAsync(device, Channel(device, server.Port, 450, 200), TunerPriority.Recording, cancellationToken);
        Assert.IsType<TunerUnavailableException>(viewing.Session.Failure);
        Assert.NotSame(viewing.Session, recording.Session);
        await Assert.ThrowsAsync<TunerUnavailableException>(() => manager.AcquireAsync(device, Channel(device, server.Port, 460, 300), TunerPriority.Recording, cancellationToken));
        Assert.True(recording.Session.IsRunning);
    }

    [Fact]
    public async Task RecordingInterruptsAnIdleScanStillOpeningItsRtspSession()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = new LoopbackRtspServer(deferFirstSetup: true);
        await using var manager = new SatIpSessionManager(31400, 31527);
        var device = Device(server.Port, 1);
        var scanning = manager.AcquireAsync(device, Channel(device, server.Port, 444, 100), TunerPriority.GuideScan, cancellationToken);
        await server.FirstSetup.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        await using var recording = await manager.AcquireAsync(device, Channel(device, server.Port, 450, 200), TunerPriority.Recording, cancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        await Assert.ThrowsAsync<TunerUnavailableException>(() => scanning);
        Assert.True(recording.Session.IsRunning);
    }

    private static SatIpDevice Device(int port, int tuners) => new(Guid.NewGuid(), "uuid:test", "Synthetic SAT>IP", new Uri($"http://127.0.0.1:{port}/satipdesc.xml"), null, null, tuners);

    private static TvChannel Channel(SatIpDevice device, int port, int frequency, int pid) => new(pid.ToString(System.Globalization.CultureInfo.InvariantCulture), device.Id, "Synthetic channel", ChannelKind.Television, new Uri(FormattableString.Invariant($"rtsp://127.0.0.1:{port}/?freq={frequency}&msys=dvbc&pids=0,{pid}")));
}
