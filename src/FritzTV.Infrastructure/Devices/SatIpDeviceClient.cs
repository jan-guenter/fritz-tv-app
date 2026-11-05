using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FritzTV.Core.Models;

namespace FritzTV.Infrastructure.Devices;

public sealed class SatIpDeviceClient(HttpClient httpClient)
{
    private const string SatIpService = "urn:ses-com:service:satip:1";
    private const int MaximumDocumentSize = 8 * 1024 * 1024;

    public async Task<SatIpDevice> GetDeviceAsync(Uri descriptorUri, CancellationToken cancellationToken = default)
    {
        ValidateHttpUri(descriptorUri);
        var document = ParseXml(await ReadTextAsync(descriptorUri, cancellationToken).ConfigureAwait(false));
        var device = document.Descendants().FirstOrDefault(element => string.Equals(element.Name.LocalName, "device", StringComparison.Ordinal))
            ?? throw new InvalidDataException("The descriptor has no UPnP device.");
        var deviceType = Child(device, "deviceType");
        if (!deviceType.Contains("SatIPServer", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The UPnP device is not a SAT>IP server.");
        }

        var udn = Child(device, "UDN");
        if (string.IsNullOrWhiteSpace(udn))
        {
            throw new InvalidDataException("The SAT>IP device has no UDN.");
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(udn));
        var id = new Guid(digest.AsSpan(0, 16));
        var baseText = document.Root?.Elements().FirstOrDefault(element => string.Equals(element.Name.LocalName, "URLBase", StringComparison.Ordinal))?.Value;
        var baseUri = string.IsNullOrWhiteSpace(baseText) ? descriptorUri : ResolveHttpUri(descriptorUri, baseText);
        var playlistText = device.Elements().FirstOrDefault(element => string.Equals(element.Name.LocalName, "X_SATIPM3U", StringComparison.Ordinal))?.Value;
        var capabilities = device.Elements().FirstOrDefault(element => string.Equals(element.Name.LocalName, "X_SATIPCAP", StringComparison.Ordinal))?.Value ?? string.Empty;
        var tunerCount = 0;
        foreach (var capability in capabilities.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = capability.LastIndexOf('-');
            if (separator >= 0 && int.TryParse(capability.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is > 0 and <= 64)
            {
                tunerCount += count;
            }
        }

        var service = device.Descendants().FirstOrDefault(element => string.Equals(element.Name.LocalName, "service", StringComparison.Ordinal) && string.Equals(Child(element, "serviceType"), SatIpService, StringComparison.Ordinal));
        var controlText = service is null ? null : Child(service, "controlURL");
        return new SatIpDevice(id, udn, Child(device, "friendlyName"), descriptorUri,
            string.IsNullOrWhiteSpace(playlistText) ? null : ResolveHttpUri(baseUri, playlistText),
            string.IsNullOrWhiteSpace(controlText) ? null : ResolveHttpUri(baseUri, controlText),
            tunerCount == 0 ? 1 : tunerCount);
    }

    public async Task<IReadOnlyList<TvChannel>> GetChannelsAsync(SatIpDevice device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var channels = new List<TvChannel>();
        if (device.ControlUri is not null)
        {
            foreach (var (action, kind) in new[]
            {
                ("X_AVM-DE_GetChannelList_m3u_HD", ChannelKind.Television),
                ("X_AVM-DE_GetChannelList_m3u_SD", ChannelKind.Television),
                ("X_AVM-DE_GetChannelList_m3u_Radio", ChannelKind.Radio),
            })
            {
                var uri = await GetPlaylistUriAsync(device.ControlUri, action, cancellationToken).ConfigureAwait(false);
                if (uri is not null)
                {
                    var content = await ReadTextAsync(uri, cancellationToken).ConfigureAwait(false);
                    channels.AddRange(M3uPlaylistParser.Parse(content, device.Id, kind));
                }
            }
        }

        if (channels.Count == 0 && device.PlaylistUri is not null)
        {
            channels.AddRange(M3uPlaylistParser.Parse(await ReadTextAsync(device.PlaylistUri, cancellationToken).ConfigureAwait(false), device.Id, ChannelKind.Television));
        }

        return channels.DistinctBy(channel => channel.Id, StringComparer.Ordinal).ToArray();
    }

    private static string Child(XElement parent, string localName) => parent.Elements().FirstOrDefault(element => string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal))?.Value ?? string.Empty;

    private static void ValidateHttpUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Use an absolute HTTP or HTTPS device URL without embedded credentials.", nameof(uri));
        }
    }

    private static Uri ResolveHttpUri(Uri baseUri, string value)
    {
        var uri = new Uri(baseUri, value);
        ValidateHttpUri(uri);
        return uri;
    }

    private static XDocument ParseXml(string text)
    {
        using var input = new StringReader(text);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumDocumentSize,
        });
        return XDocument.Load(reader);
    }

    private async Task<Uri?> GetPlaylistUriAsync(Uri controlUri, string action, CancellationToken cancellationToken)
    {
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace service = SatIpService;
        var body = new XDocument(new XElement(soap + "Envelope", new XAttribute(XNamespace.Xmlns + "s", soap), new XElement(soap + "Body", new XElement(service + action))));
        using var request = new HttpRequestMessage(HttpMethod.Post, controlUri);
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{SatIpService}#{action}\"");
        request.Content = new StringContent(body.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var xml = ParseXml(await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false));
        var value = xml.Descendants().FirstOrDefault(element => string.Equals(element.Name.LocalName, "NewX_AVM-DE_Url", StringComparison.Ordinal))?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : ResolveHttpUri(controlUri, value);
    }

    private async Task<string> ReadTextAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumDocumentSize)
        {
            throw new InvalidDataException("The device response exceeds the 8 MiB limit.");
        }

        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var streamScope = stream.ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            if (output.Length + count > MaximumDocumentSize)
            {
                throw new InvalidDataException("The device response exceeds the 8 MiB limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }
}
