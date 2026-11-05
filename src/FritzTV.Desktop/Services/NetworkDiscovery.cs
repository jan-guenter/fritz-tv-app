using System.Net.NetworkInformation;
using System.Net.Sockets;
using FritzTV.Infrastructure.Devices;

namespace FritzTV.Desktop.Services;

public static class NetworkDiscovery
{
    public static IReadOnlyList<DiscoveryInterface> GetInterfaces(string automaticLabel)
    {
        var interfaces = new List<DiscoveryInterface> { new(automaticLabel, null) };
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces().Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses.Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork))
                {
                    interfaces.Add(new DiscoveryInterface(adapter.Name + " · " + address.Address, address.Address));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // The default discovery route remains available when interface enumeration is restricted.
        }

        return interfaces;
    }

    public static Task<IReadOnlyList<Uri>> DiscoverAsync(DiscoveryInterface? networkInterface, CancellationToken cancellationToken) =>
        new SsdpDiscovery(networkInterface?.Address).DiscoverAsync(TimeSpan.FromSeconds(3), cancellationToken);
}
