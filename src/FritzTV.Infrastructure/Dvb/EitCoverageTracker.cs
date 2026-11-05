namespace FritzTV.Infrastructure.Dvb;

public sealed class EitCoverageTracker
{
    private readonly Dictionary<(ushort Service, byte Table), TableCoverage> tables = [];

    public bool IsComplete => tables.Count > 0 && tables.Values.All(table => table.Complete);

    public void ExpectServices(IEnumerable<ushort> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var service in services)
        {
            if (tables.Count < 8192)
            {
                tables.TryAdd((service, 0x50), new TableCoverage(-1, 0));
            }
        }
    }

    public void Observe(ReadOnlySpan<byte> section)
    {
        if (section.Length < 18 || section[0] is < 0x50 or > 0x5f || (section[5] & 1) == 0 || section[6] > section[7]
            || section.Length != 3 + (((section[1] & 15) << 8) | section[2]) || MpegCrc32.Compute(section) != 0)
        {
            return;
        }

        var serviceId = (ushort)((section[3] << 8) | section[4]);
        var lastTable = section[13];
        if (lastTable < section[0] || lastTable > 0x5f)
        {
            return;
        }

        for (var expectedTable = 0x50; expectedTable <= lastTable; expectedTable++)
        {
            if (tables.Count < 8192)
            {
                tables.TryAdd((serviceId, (byte)expectedTable), new TableCoverage(-1, 0));
            }
        }

        var key = (serviceId, section[0]);
        var version = (section[5] >> 1) & 31;
        if (!tables.TryGetValue(key, out var table))
        {
            return;
        }

        if (table.Version != version)
        {
            table = new TableCoverage(version, section[7]);
            tables[key] = table;
        }

        table.Sections.Add(section[6]);
        table.Segments[section[6] / 8] = section[12];
    }

    private sealed class TableCoverage(int version, int lastSection)
    {
        public int Version { get; } = version;

        public HashSet<int> Sections { get; } = [];

        public Dictionary<int, int> Segments { get; } = [];

        public bool Complete
        {
            get
            {
                if (Version < 0)
                {
                    return false;
                }

                for (var first = 0; first <= lastSection; first += 8)
                {
                    if (!Segments.TryGetValue(first / 8, out var last) || last < first || last > Math.Min(first + 7, lastSection))
                    {
                        return false;
                    }

                    for (var section = first; section <= last; section++)
                    {
                        if (!Sections.Contains(section))
                        {
                            return false;
                        }
                    }
                }

                return true;
            }
        }
    }
}
