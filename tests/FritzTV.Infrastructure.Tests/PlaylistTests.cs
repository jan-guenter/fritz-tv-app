using System.Text;
using FritzTV.Core.Models;
using FritzTV.Infrastructure.Devices;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class PlaylistTests
{
    [Fact]
    public void ParserPreservesRadioNamesAndDeduplicatesUris()
    {
        var id = Guid.NewGuid();
        var channels = M3uPlaylistParser.Parse("#EXTM3U\n#EXTINF:0,Radio Köln\n#EXTVLCOPT:network-caching=1000\nrtsp://192.0.2.1/?freq=386&pids=0,18,100\n#EXTINF:0,Duplicate\nrtsp://192.0.2.1/?freq=386&pids=0,18,100", id, ChannelKind.Radio);
        var channel = Assert.Single(channels);
        Assert.Equal("Radio Köln", channel.Name);
        Assert.Equal(ChannelKind.Radio, channel.Kind);
        Assert.Equal(id, channel.DeviceId);
    }

    [Fact]
    public void ParserRejectsNonRtspUrlsWithoutReusingTheirName()
    {
        var channels = M3uPlaylistParser.Parse("#EXTINF:0,Unsafe\nfile:///etc/passwd\nrtsp://192.0.2.1/?freq=386", Guid.NewGuid(), ChannelKind.Television);
        Assert.Equal("rtsp://192.0.2.1/?freq=386", Assert.Single(channels).Name);
    }

    [Fact]
    public void SsdpAcceptsCaseInsensitiveLocationAndRejectsFileUris()
    {
        var location = SsdpDiscovery.ParseLocation(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nlocation: http://192.0.2.1:49000/satipdesc.xml\r\n\r\n"));
        Assert.Equal(new Uri("http://192.0.2.1:49000/satipdesc.xml"), location);
        Assert.Null(SsdpDiscovery.ParseLocation(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nLOCATION: file:///secret\r\n\r\n")));
    }
}
