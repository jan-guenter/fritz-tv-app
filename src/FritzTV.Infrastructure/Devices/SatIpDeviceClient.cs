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
        if (!deviceType.StartsWith("urn:ses-com:device:SatIPServer:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The UPnP device is not a SAT>IP server.");
        }

        var udn = Child(device, "UDN").Trim();
        if (string.IsNullOrWhiteSpace(udn))
        {
            throw new InvalidDataException("The SAT>IP device has no UDN.");
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(udn.ToLowerInvariant()));
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
            Math.Clamp(tunerCount, 1, 64));
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

    public async Task<IReadOnlyList<TunerStatus>> GetTunerStatusAsync(SatIpDevice device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var result = new List<TunerStatus>();
        if (device.ControlUri is null)
        {
            return result;
        }

        for (var number = 0; number < Math.Min(device.TunerCount, 64); number++)
        {
            var response = await InvokeActionAsync(device.ControlUri, "X_AVM-DE_GetTunerInfo", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["NewX_AVM-DE_TunerNumber"] = number.ToString(CultureInfo.InvariantCulture),
            }, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                break;
            }

            var fields = response.Descendants().Where(element => !element.HasElements).ToDictionary(element => element.Name.LocalName, element => element.Value, StringComparer.Ordinal);
            result.Add(new TunerStatus(number, IsTrue(fields.GetValueOrDefault("NewX_AVM-DE_Used")), IsTrue(fields.GetValueOrDefault("NewX_AVM-DE_HasLock")),
                fields.GetValueOrDefault("NewX_AVM-DE_SignalPower") ?? string.Empty, fields.GetValueOrDefault("NewX_AVM-DE_SignalNoiseRatio") ?? string.Empty,
                fields.GetValueOrDefault("NewX_AVM-DE_ChannelName") ?? string.Empty,
                int.TryParse(fields.GetValueOrDefault("NewX_AVM-DE_ClientCount"), NumberStyles.None, CultureInfo.InvariantCulture, out var clients) ? clients : 0));
        }

        return result;
    }

    private static bool IsTrue(string? value) => string.Equals(value, "1", StringComparison.Ordinal) || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private async Task<Uri?> GetPlaylistUriAsync(Uri controlUri, string action, CancellationToken cancellationToken)
    {
        var response = await InvokeActionAsync(controlUri, action, null, cancellationToken).ConfigureAwait(false);
        var value = response?.Descendants().FirstOrDefault(element => string.Equals(element.Name.LocalName, "NewX_AVM-DE_Url", StringComparison.Ordinal))?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : ResolveHttpUri(controlUri, value);
    }

    private async Task<XDocument?> InvokeActionAsync(Uri controlUri, string action, IReadOnlyDictionary<string, string>? arguments, CancellationToken cancellationToken)
    {
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace service = SatIpService;
        var invocation = new XElement(service + action);
        if (arguments is not null)
        {
            foreach (var (name, value) in arguments)
            {
                invocation.Add(new XElement(name, value));
            }
        }

        var body = new XDocument(new XElement(soap + "Envelope", new XAttribute(XNamespace.Xmlns + "s", soap), new XElement(soap + "Body", invocation)));
        using var request = new HttpRequestMessage(HttpMethod.Post, controlUri);
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{SatIpService}#{action}\"");
        request.Content = new StringContent(body.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml");
        var response = await ReadResponseAsync(request, allowFailure: true, cancellationToken).ConfigureAwait(false);
        return response is null ? null : ParseXml(response);
    }

    private async Task<string> ReadTextAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        return await ReadResponseAsync(request, allowFailure: false, cancellationToken).ConfigureAwait(false) ?? string.Empty;
    }

    private async Task<string?> ReadResponseAsync(HttpRequestMessage request, bool allowFailure, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var duration = httpClient.Timeout > TimeSpan.Zero && httpClient.Timeout < TimeSpan.FromSeconds(15) ? httpClient.Timeout : TimeSpan.FromSeconds(15);
        deadline.CancelAfter(duration);
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (allowFailure)
                {
                    return null;
                }

                response.EnsureSuccessStatusCode();
            }

            return await ReadBoundedAsync(response.Content, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && (deadline.IsCancellationRequested || exception.InnerException is TimeoutException))
        {
            throw new TimeoutException("The SAT>IP device did not finish its response before the request deadline.", exception);
        }
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
