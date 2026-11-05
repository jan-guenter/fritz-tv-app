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
    public async Task UdnDeduplicationIgnoresUuidLetterCaseAndSurroundingWhitespace()
    {
        using var handler = new FixtureHandler(request => Response(string.Equals(request.RequestUri!.Host, "192.0.2.1", StringComparison.Ordinal) ? Descriptor : Descriptor.Replace("uuid:synthetic-device", " UUID:SYNTHETIC-DEVICE ", StringComparison.Ordinal)));
        using var http = new HttpClient(handler);
        var client = new SatIpDeviceClient(http);
        var first = await client.GetDeviceAsync(new Uri("http://192.0.2.1/descriptor.xml"), TestContext.Current.CancellationToken);
        var second = await client.GetDeviceAsync(new Uri("http://192.0.2.2/descriptor.xml"), TestContext.Current.CancellationToken);
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task DescriptorRejectsExternalEntities()
    {
        using var handler = new FixtureHandler(_ => Response("<!DOCTYPE root [<!ENTITY secret SYSTEM 'file:///etc/passwd'>]><root>&secret;</root>"));
        using var http = new HttpClient(handler);
        var client = new SatIpDeviceClient(http);
        await Assert.ThrowsAsync<XmlException>(() => client.GetDeviceAsync(new Uri("http://192.0.2.1/descriptor.xml"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WholeResponseDeadlineIncludesStalledDescriptorAndSoapBodies(bool soap)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromSeconds(3));
        using var handler = new FixtureHandler(request => !soap || request.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingReadStream()) } : Response(Descriptor));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(200) };
        var client = new SatIpDeviceClient(http);
        var uri = new Uri("http://192.0.2.1/device.xml");
        if (soap)
        {
            var device = await client.GetDeviceAsync(uri, caller.Token);
            await Assert.ThrowsAsync<TimeoutException>(() => client.GetChannelsAsync(device, caller.Token));
        }
        else
        {
            await Assert.ThrowsAsync<TimeoutException>(() => client.GetDeviceAsync(uri, caller.Token));
        }
    }

    [Fact]
    public async Task ExplicitCallerCancellationIsPreservedDuringAStalledBody()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromSeconds(3));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingReadStream(started)) });
        using var http = new HttpClient(handler);
        var client = new SatIpDeviceClient(http);
        var request = client.GetDeviceAsync(new Uri("http://192.0.2.1/device.xml"), caller.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };

    private sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
