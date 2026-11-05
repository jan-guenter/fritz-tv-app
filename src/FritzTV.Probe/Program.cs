using System.Text.Json;
using System.Xml;
using FritzTV.Infrastructure.Devices;

namespace FritzTV.Probe;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            if (options.ContainsKey("help"))
            {
                Console.WriteLine("FritzTV.Probe --device http://host:49000/satipdesc.xml [--list] [--channel name] [--duration 10] [--first-port 50000] [--last-port 50031] [--capture path.ts]");
                return 0;
            }

            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
            var client = new SatIpDeviceClient(http);
            var descriptor = new Uri(options.GetValueOrDefault("device", "http://192.168.0.1:49000/satipdesc.xml"));
            var device = await client.GetDeviceAsync(descriptor).ConfigureAwait(false);
            var channels = await client.GetChannelsAsync(device).ConfigureAwait(false);
            if (options.ContainsKey("list"))
            {
                WriteJson(new { Device = device, Channels = channels });
                return 0;
            }

            var channelName = options.GetValueOrDefault("channel");
            var channel = channelName is null ? (channels.Count > 0 ? channels[0] : null) : channels.FirstOrDefault(item => string.Equals(item.Name, channelName, StringComparison.OrdinalIgnoreCase));
            if (channel is null)
            {
                throw new ArgumentException("The requested channel was not found. Use --list to inspect available channels.", nameof(args));
            }

            var report = await ProbeCapture.RunAsync(device, channel, options).ConfigureAwait(false);
            WriteJson(report);
            return report.TransportPacketCount == 0 ? 2 : 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException or ArgumentException or InvalidOperationException or TimeoutException or XmlException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Options must start with --.", nameof(args));
            }

            var key = args[index][2..];
            if (key is "help" or "list")
            {
                result[key] = "true";
            }
            else if (++index < args.Length)
            {
                result[key] = args[index];
            }
            else
            {
                throw new ArgumentException($"Option --{key} requires a value.", nameof(args));
            }
        }

        return result;
    }

    private static void WriteJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
}
