using FritzTV.Core.Models;

namespace FritzTV.Desktop.Services;

public sealed record ReceptionSnapshot(IReadOnlyList<TunerUsage> Tuners, ReceptionStatus? Signal, long RtpPackets, long LostPackets);
