namespace FritzTV.Core.Models;

public sealed record SatIpDevice(
    Guid Id,
    string Udn,
    string Name,
    Uri DescriptorUri,
    Uri? PlaylistUri,
    Uri? ControlUri,
    int TunerCount);
