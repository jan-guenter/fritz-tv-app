using System.Net;
using System.Net.Sockets;
using System.Text;
using FritzTV.Infrastructure.Devices;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class SsdpNetworkTests
{
    [Fact]
    [Trait("Category", "LiveNetwork")]
    public async Task SearchLeavesThroughSelectedMulticastInterfaceAndDeduplicatesResponses()
    {
        var configured = Environment.GetEnvironmentVariable("FRITZTV_SSDP_TEST_ADDRESS");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Skip("Set FRITZTV_SSDP_TEST_ADDRESS to an IPv4 address on a reachable multicast LAN.");
        }

        Assert.True(IPAddress.TryParse(configured, out var address) && address.AddressFamily == AddressFamily.InterNetwork);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var observer = new UdpClient(AddressFamily.InterNetwork);
        observer.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        observer.Client.Bind(new IPEndPoint(IPAddress.Any, 1900));
        observer.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"), address);
        var discovery = new SsdpDiscovery(address).DiscoverAsync(TimeSpan.FromSeconds(2), deadline.Token);
        UdpReceiveResult request;
        do
        {
            request = await observer.ReceiveAsync(deadline.Token);
        }
        while (!Encoding.ASCII.GetString(request.Buffer).Contains("ST: urn:ses-com:device:SatIPServer:1", StringComparison.Ordinal));
        Assert.Equal(address, request.RemoteEndPoint.Address);
        Assert.StartsWith("M-SEARCH * HTTP/1.1\r\n", Encoding.ASCII.GetString(request.Buffer), StringComparison.Ordinal);
        var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nLOCATION: http://192.0.2.1/synthetic-device.xml\r\n\r\n");
        await observer.SendAsync(response, request.RemoteEndPoint, deadline.Token);
        await observer.SendAsync(response, request.RemoteEndPoint, deadline.Token);
        Assert.Equal(new Uri("http://192.0.2.1/synthetic-device.xml"), Assert.Single(await discovery));
    }
}
