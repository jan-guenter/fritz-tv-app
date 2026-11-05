using System.Net;

namespace FritzTV.Desktop.Services;

public sealed record DiscoveryInterface(string Name, IPAddress? Address)
{
    public override string ToString() => Name;
}
