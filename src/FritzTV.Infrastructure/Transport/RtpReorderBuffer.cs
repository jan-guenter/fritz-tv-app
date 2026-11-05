namespace FritzTV.Infrastructure.Transport;

public sealed class RtpReorderBuffer(int capacity = 256, TimeSpan? maximumDelay = null, TimeProvider? timeProvider = null)
{
    private readonly SortedDictionary<long, (RtpPacket Packet, long Received)> pending = [];
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan delay = maximumDelay ?? TimeSpan.FromMilliseconds(200);
    private readonly int maximumPending = ValidateCapacity(capacity);
    private long? highest;
    private long expected;
    private uint? source;

    public long LostPackets { get; private set; }

    public long DuplicatePackets { get; private set; }

    public IReadOnlyList<RtpPacket> Push(RtpPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var result = new List<RtpPacket>();
        if (source != packet.Source)
        {
            pending.Clear();
            highest = null;
            source = packet.Source;
        }

        var sequence = highest is { } previous ? previous + (short)(packet.Sequence - (ushort)previous) : packet.Sequence;
        if (highest is null)
        {
            expected = sequence;
        }

        highest = Math.Max(highest ?? sequence, sequence);
        if (sequence < expected || !pending.TryAdd(sequence, (packet, clock.GetTimestamp())))
        {
            DuplicatePackets++;
            return result;
        }

        while (pending.Count > 0)
        {
            if (pending.Remove(expected, out var ready))
            {
                result.Add(ready.Packet);
                expected++;
                continue;
            }

            var first = pending.First();
            if (pending.Count <= maximumPending && clock.GetElapsedTime(first.Value.Received) < delay)
            {
                break;
            }

            LostPackets += first.Key - expected;
            expected = first.Key;
        }

        return result;
    }
    private static int ValidateCapacity(int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        return value;
    }

}
