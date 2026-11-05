using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FritzTV.Core.Models;

public static class ChannelIdentity
{
    public static string ForService(DvbServiceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var key = string.Create(CultureInfo.InvariantCulture, $"dvb:{identity.DeviceId:D}:{identity.OriginalNetworkId}:{identity.TransportStreamId}:{identity.ServiceId}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }
}
