using FritzTV.Core.Models;

namespace FritzTV.Probe;

internal sealed record ProbeReport(
    DateTimeOffset TimestampUtc,
    string Runtime,
    string OperatingSystem,
    string Device,
    int TunerCount,
    string Channel,
    int DurationSeconds,
    long ReceivedPackets,
    long ReceivedBytes,
    long RtcpPackets,
    ReceptionStatus? ReceptionStatus,
    string? RtcpFailure,
    long LostPackets,
    string? Failure,
    long TransportPacketCount,
    IReadOnlyDictionary<int, long> Pids,
    IReadOnlyDictionary<string, long> Tables,
    IReadOnlyDictionary<int, int> Programs,
    IReadOnlyDictionary<int, int> StreamTypes);
