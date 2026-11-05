using System.Security.Cryptography;
using System.Text;
using FritzTV.Core.Models;

namespace FritzTV.Infrastructure.Devices;

public static class M3uPlaylistParser
{
    public static IReadOnlyList<TvChannel> Parse(string content, Guid deviceId, ChannelKind kind)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length > 8 * 1024 * 1024)
        {
            throw new InvalidDataException("The playlist exceeds the 8 MiB limit.");
        }

        var channels = new List<TvChannel>();
        string? name = null;
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim().TrimStart('\uFEFF');
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = line.IndexOf(',');
                name = comma >= 0 ? line[(comma + 1)..].Trim() : null;
                continue;
            }

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase))
            {
                name = null;
                continue;
            }

            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{deviceId:D}|{uri.AbsoluteUri}")));
            channels.Add(new TvChannel(identity, deviceId, name ?? uri.AbsoluteUri, kind, uri));
            name = null;
        }

        return channels.DistinctBy(channel => channel.Id, StringComparer.Ordinal).ToArray();
    }
}
