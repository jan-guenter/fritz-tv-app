using System.Net;
using System.Text;
using System.Xml;
using FritzTV.Infrastructure.Devices;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class DeviceClientTests
{
    private const string Descriptor = """
        <root xmlns="urn:schemas-upnp-org:device-1-0"><device>
        <deviceType>urn:ses-com:device:SatIPServer:1</deviceType><friendlyName>Test &amp; TV</friendlyName>
        <UDN>uuid:synthetic-device</UDN><serviceList><service><serviceType>urn:ses-com:service:satip:1</serviceType><controlURL>/upnp/control/satip</controlURL></service></serviceList>
        <satip:X_SATIPCAP xmlns:satip="urn:ses-com:satip">DVBC-4</satip:X_SATIPCAP>
        <satip:X_SATIPM3U xmlns:satip="urn:ses-com:satip">http://192.0.2.1/dvb/m3u/channels.m3u</satip:X_SATIPM3U>
        </device></root>
        """;

    [Fact]
    public async Task DescriptorResolvesControlPortAndAdvertisedPlaylistIndependently()
    {
        using var handler = new FixtureHandler(_ => Response(Descriptor));
        using var http = new HttpClient(handler);
        var client = new SatIpDeviceClient(http);
        var device = await client.GetDeviceAsync(new Uri("http://192.0.2.1:49000/satipdesc.xml"), TestContext.Current.CancellationToken);
        Assert.Equal("Test & TV", device.Name);
        Assert.Equal(4, device.TunerCount);
        Assert.Equal(new Uri("http://192.0.2.1:49000/upnp/control/satip"), device.ControlUri);
        Assert.Equal(new Uri("http://192.0.2.1/dvb/m3u/channels.m3u"), device.PlaylistUri);
    }

    [Fact]
    public async Task DescriptorRejectsExternalEntities()
    {
        using var handler = new FixtureHandler(_ => Response("<!DOCTYPE root [<!ENTITY secret SYSTEM 'file:///etc/passwd'>]><root>&secret;</root>"));
        using var http = new HttpClient(handler);
        var client = new SatIpDeviceClient(http);
        await Assert.ThrowsAsync<XmlException>(() => client.GetDeviceAsync(new Uri("http://192.0.2.1/descriptor.xml"), TestContext.Current.CancellationToken));
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };

    private sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
