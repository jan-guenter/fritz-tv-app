using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace FritzTV.Infrastructure.Devices;

public sealed class SsdpDiscovery(IPAddress? localAddress = null)
{
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("239.255.255.250"), 1900);
    private static readonly byte[] Search = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: urn:ses-com:device:SatIPServer:1\r\n\r\n");

    public async Task<IReadOnlyList<Uri>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var addresses = localAddress is not null ? [localAddress] : NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up && item.SupportsMulticast)
            .SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Select(item => item.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
            .Distinct().ToArray();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var found = await Task.WhenAll(addresses.Select(address => SearchInterfaceAsync(address, deadline.Token))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return found.SelectMany(item => item).Distinct().ToArray();
    }

    public static Uri? ParseLocation(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length is 0 or > 16384)
        {
            return null;
        }

        var response = Encoding.ASCII.GetString(datagram);
        if (!response.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var line in response.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line.AsSpan(0, colon).Trim().Equals("LOCATION", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(line[(colon + 1)..].Trim(), UriKind.Absolute, out var uri)
                && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) && string.IsNullOrEmpty(uri.UserInfo))
            {
                return uri;
            }
        }

        return null;
    }

    private static async Task<IReadOnlyList<Uri>> SearchInterfaceAsync(IPAddress address, CancellationToken cancellationToken)
    {
        var found = new HashSet<Uri>();
        using var client = new UdpClient(new IPEndPoint(address, 0));
        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
        try
        {
            await client.SendAsync(Search, MulticastEndpoint, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (ParseLocation(result.Buffer) is { } uri)
                {
                    found.Add(uri);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // An SSDP search completes normally at its deadline.
        }
        catch (SocketException)
        {
            // One unavailable network interface must not hide devices on the others.
        }

        return found.ToArray();
    }
}
