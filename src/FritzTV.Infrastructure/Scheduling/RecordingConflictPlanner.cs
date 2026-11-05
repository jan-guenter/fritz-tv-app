using FritzTV.Core.Models;
using FritzTV.Infrastructure.Transport;

namespace FritzTV.Infrastructure.Scheduling;

public static class RecordingConflictPlanner
{
    public static IReadOnlySet<Guid> FindConflicts(IEnumerable<RecordingInstance> instances, IReadOnlyList<TvChannel> channels, IReadOnlyList<SatIpDevice> devices, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(devices);
        var candidates = instances.Where(item => item.End > now && item.State is RecordingState.Scheduled or RecordingState.Conflict or RecordingState.Recording).ToArray();
        var conflicts = new HashSet<Guid>();
        foreach (var device in devices)
        {
            var matching = candidates.Select(instance => (Instance: instance, Channel: channels.FirstOrDefault(channel => string.Equals(channel.Id, instance.ChannelId, StringComparison.Ordinal))))
                .Where(item => item.Channel?.DeviceId == device.Id).ToArray();
            var events = matching.SelectMany(item => new[]
            {
                new Boundary(item.Instance.Start < now ? now : item.Instance.Start, true, item.Instance, SatIpTuning.Key(item.Channel!.StreamUri)),
                new Boundary(item.Instance.End, false, item.Instance, SatIpTuning.Key(item.Channel!.StreamUri)),
            }).OrderBy(item => item.At).ThenBy(item => item.IsStart).ThenByDescending(item => item.Instance.State == RecordingState.Recording)
                .ThenBy(item => item.Instance.Start).ThenBy(item => item.Instance.Created).ThenBy(item => item.Instance.Id);
            var allocated = new Dictionary<string, int>(StringComparer.Ordinal);
            var accepted = new HashSet<Guid>();
            foreach (var boundary in events)
            {
                ApplyBoundary(boundary, Math.Max(1, device.TunerCount), allocated, accepted, conflicts);
            }
        }

        return conflicts;
    }

    private static void ApplyBoundary(Boundary boundary, int capacity, Dictionary<string, int> allocated, HashSet<Guid> accepted, HashSet<Guid> conflicts)
    {
        if (!boundary.IsStart)
        {
            if (accepted.Remove(boundary.Instance.Id) && --allocated[boundary.Tuning] == 0)
            {
                allocated.Remove(boundary.Tuning);
            }

            return;
        }

        if (boundary.Instance.State == RecordingState.Recording || allocated.ContainsKey(boundary.Tuning) || allocated.Count < capacity)
        {
            accepted.Add(boundary.Instance.Id);
            allocated[boundary.Tuning] = allocated.GetValueOrDefault(boundary.Tuning) + 1;
        }
        else
        {
            conflicts.Add(boundary.Instance.Id);
        }
    }

    private sealed record Boundary(DateTimeOffset At, bool IsStart, RecordingInstance Instance, string Tuning);
}
