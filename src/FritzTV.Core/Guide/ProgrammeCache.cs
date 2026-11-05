using FritzTV.Core.Models;

namespace FritzTV.Core.Guide;

public sealed class ProgrammeCache(int capacity = 100000, TimeProvider? timeProvider = null)
{
    private readonly int maximumCount = ValidateCapacity(capacity);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Lock gate = new();
    private readonly Dictionary<(DvbServiceIdentity Service, ushort EventId), Programme> programmes = [];
    private DateTimeOffset nextPrune;

    public IReadOnlyList<Programme> Snapshot()
    {
        lock (gate)
        {
            Prune(clock.GetUtcNow());
            return programmes.Values.OrderBy(programme => programme.Start).ToArray();
        }
    }

    public void Ingest(IEnumerable<Programme> incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        var now = clock.GetUtcNow();
        lock (gate)
        {
            Prune(now);
            foreach (var programme in incoming)
            {
                var key = (programme.Service, programme.EventId);
                if (programme.End < now.AddDays(-1) || programme.Start > now.AddDays(14))
                {
                    continue;
                }

                if (programmes.TryGetValue(key, out var previous))
                {
                    if (ProgrammeUpdatePolicy.ShouldReplace(previous, programme, now))
                    {
                        programmes[key] = programme;
                    }
                }
                else if (programmes.Count < maximumCount)
                {
                    programmes.Add(key, programme);
                }
            }
        }
    }

    private void Prune(DateTimeOffset now)
    {
        if (now < nextPrune)
        {
            return;
        }

        foreach (var expired in programmes.Where(item => item.Value.End < now.AddDays(-1) || item.Value.Start > now.AddDays(14)).Select(item => item.Key).ToArray())
        {
            programmes.Remove(expired);
        }

        nextPrune = now.AddMinutes(1);
    }

    private static int ValidateCapacity(int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        return value;
    }
}
