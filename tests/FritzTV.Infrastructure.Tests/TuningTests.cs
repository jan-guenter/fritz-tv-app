using FritzTV.Infrastructure.Transport;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class TuningTests
{
    [Fact]
    public void PidSerializationPreservesSatIpCommaSeparators()
    {
        var original = new Uri("rtsp://192.0.2.1:554/?avm=1&freq=386&pids=0,18,5100,5101");
        var rewritten = SatIpTuning.WithPids(original, SatIpTuning.Pids(original));
        Assert.Contains("pids=0,16,17,18,20,5100,5101", rewritten.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("%2C", rewritten.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TuningKeyExcludesPidSelectionButRetainsFrequency()
    {
        var first = new Uri("rtsp://192.0.2.1/?freq=386&msys=dvbc&pids=0,18,100");
        var sameMultiplex = new Uri("rtsp://192.0.2.1/?msys=dvbc&freq=386&pids=0,18,200");
        var otherMultiplex = new Uri("rtsp://192.0.2.1/?freq=450&msys=dvbc&pids=0,18,100");
        Assert.Equal(SatIpTuning.Key(first), SatIpTuning.Key(sameMultiplex), StringComparer.Ordinal);
        Assert.NotEqual(SatIpTuning.Key(first), SatIpTuning.Key(otherMultiplex), StringComparer.Ordinal);
    }
}
