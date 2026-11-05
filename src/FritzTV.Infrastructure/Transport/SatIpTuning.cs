using System.Globalization;

namespace FritzTV.Infrastructure.Transport;

public static class SatIpTuning
{
    public static string Key(Uri streamUri)
    {
        ArgumentNullException.ThrowIfNull(streamUri);
        var values = Parameters(streamUri).Where(item => !item.Key.EndsWith("pids", StringComparison.OrdinalIgnoreCase)).OrderBy(item => item.Key, StringComparer.Ordinal);
        return new UriBuilder(streamUri) { Query = string.Join('&', values.Select(item => Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(item.Value))) }.Uri.AbsoluteUri;
    }

    public static IReadOnlySet<int> Pids(Uri streamUri)
    {
        var result = new HashSet<int> { 0, 16, 17, 18, 20 };
        if (Parameters(streamUri).TryGetValue("pids", out var value))
        {
            foreach (var text in value.Split(','))
            {
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid is < 0 or > 8191)
                {
                    throw new InvalidDataException("The stream URL has an invalid PID list.");
                }

                result.Add(pid);
            }
        }

        return result;
    }

    public static Uri WithPids(Uri streamUri, IEnumerable<int> pids)
    {
        ArgumentNullException.ThrowIfNull(pids);
        var parameters = Parameters(streamUri);
        parameters["pids"] = string.Join(',', pids.Distinct().Order().Select(value => value.ToString(CultureInfo.InvariantCulture)));
        return new UriBuilder(streamUri) { Query = string.Join('&', parameters.Select(item => Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(item.Value).Replace("%2C", ",", StringComparison.OrdinalIgnoreCase))) }.Uri;
    }

    public static ReadOnlyMemory<byte> Filter(ReadOnlyMemory<byte> transport, IReadOnlySet<int> pids)
    {
        ArgumentNullException.ThrowIfNull(pids);
        var output = new byte[transport.Length];
        var written = 0;
        for (var offset = 0; offset + 188 <= transport.Length; offset += 188)
        {
            var packet = transport.Span.Slice(offset, 188);
            var pid = ((packet[1] & 31) << 8) | packet[2];
            if (packet[0] == 0x47 && pids.Contains(pid))
            {
                packet.CopyTo(output.AsSpan(written));
                written += 188;
            }
        }

        return output.AsMemory(0, written);
    }

    private static Dictionary<string, string> Parameters(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = parameter.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0]);
            // AVM playlist compatibility slots force unrelated sessions onto one tuner.
            // Standard SAT>IP allocation must omit this proprietary client-slot parameter.
            if (!string.Equals(key, "avm", StringComparison.OrdinalIgnoreCase))
            {
                result[key] = pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
            }
        }

        return result;
    }
}
