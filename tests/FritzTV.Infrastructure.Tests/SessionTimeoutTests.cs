using System.Diagnostics;
using FritzTV.Infrastructure.Transport;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class SessionTimeoutTests
{
    [Theory]
    [InlineData("SETUP")]
    [InlineData("PLAY")]
    public async Task AStalledHandshakeReportsTheExactPhaseWithoutExposingTheDeviceUrl(string method)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        await using var server = new LoopbackRtspServer(blockMethod: method);
        var elapsed = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<IOException>(() => SatIpSession.OpenAsync(
            new Uri(FormattableString.Invariant($"rtsp://127.0.0.1:{server.Port}/?freq=450&pids=0,100")), 34400, 34527, deadline.Token));
        Assert.Equal("The SAT>IP RTSP " + method + " timed out.", exception.Message);
        Assert.InRange(elapsed.Elapsed, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(20));
        Assert.DoesNotContain("127.0.0.1", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("freq=", exception.Message, StringComparison.Ordinal);
        Assert.Contains(server.Requests, request => request.StartsWith(method + " ", StringComparison.Ordinal));
    }
}
