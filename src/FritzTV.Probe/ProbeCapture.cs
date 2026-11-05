using System.Globalization;
using System.Runtime.InteropServices;
using FritzTV.Core.Models;
using FritzTV.Infrastructure.Transport;

namespace FritzTV.Probe;

internal static class ProbeCapture
{
    public static async Task<ProbeReport> RunAsync(SatIpDevice device, TvChannel channel, IReadOnlyDictionary<string, string> options)
    {
        var duration = Number(options, "duration", 10, 1, 3600);
        var firstPort = Number(options, "first-port", 50000, 1024, 65534);
        var lastPort = Number(options, "last-port", 50031, firstPort + 1, 65535);
        var session = await SatIpSession.OpenAsync(channel.StreamUri, firstPort, lastPort).ConfigureAwait(false);
        await using var sessionScope = session.ConfigureAwait(false);
        using var subscription = session.Subscribe();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(duration));
        FileStream? capture = null;
        if (options.TryGetValue("capture", out var path))
        {
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            capture = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
        }

        var stats = new ProbeStatistics();
        try
        {
            await foreach (var batch in subscription.Reader.ReadAllAsync(deadline.Token).ConfigureAwait(false))
            {
                if (capture is not null)
                {
                    await capture.WriteAsync(batch, deadline.Token).ConfigureAwait(false);
                }

                stats.Push(batch);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
        }
        finally
        {
            if (capture is not null)
            {
                await capture.DisposeAsync().ConfigureAwait(false);
            }
        }

        return new ProbeReport(DateTimeOffset.UtcNow, RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
            device.Name, device.TunerCount, channel.Name, duration, session.ReceivedPackets, session.ReceivedBytes, session.RtcpPackets, session.ReceptionStatus, session.RtcpFailure?.Message,
            session.LostPackets, session.Failure?.Message, stats.Pids.Values.Sum(), stats.Pids, stats.TableCounts, stats.Programs, stats.StreamTypes);
    }

    private static int Number(IReadOnlyDictionary<string, string> options, string key, int fallback, int minimum, int maximum)
    {
        var value = options.TryGetValue(key, out var text) ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;
        return value >= minimum && value <= maximum ? value : throw new ArgumentOutOfRangeException(key);
    }
}
