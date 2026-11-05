using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using FritzTV.Core.Models;

namespace FritzTV.Infrastructure.Transport;

public sealed class SatIpSession : IAsyncDisposable
{
    private readonly UdpClient rtp;
    private readonly UdpClient rtcp;
    private readonly RtspConnection connection;
    private readonly Uri sessionUri;
    private readonly string sessionId;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<Guid, Channel<ReadOnlyMemory<byte>>> consumers = [];
    private readonly Lock consumerLock = new();
    private readonly RtpReorderBuffer reorder = new();
    private readonly Task receiveTask;
    private readonly Task controlTask;
    private readonly Task rtcpTask;
    private readonly Task watchdogTask;
    private long lastPacketAt = TimeProvider.System.GetTimestamp();
    private readonly TimeSpan keepaliveInterval;
    private long receivedPackets;
    private long receivedBytes;
    private long rtcpPackets;
    private bool disposed;

    private SatIpSession(UdpClient rtp, UdpClient rtcp, RtspConnection connection, Uri sessionUri, string sessionId, TimeSpan keepaliveInterval)
    {
        this.rtp = rtp;
        this.rtcp = rtcp;
        this.connection = connection;
        this.sessionUri = sessionUri;
        this.sessionId = sessionId;
        this.keepaliveInterval = keepaliveInterval;
        receiveTask = ReceiveAsync(lifetime.Token);
        controlTask = KeepAliveAsync(lifetime.Token);
        rtcpTask = ReceiveRtcpAsync(lifetime.Token);
        watchdogTask = WatchReceptionAsync(lifetime.Token);
    }

    public long ReceivedPackets => Interlocked.Read(ref receivedPackets);

    public long ReceivedBytes => Interlocked.Read(ref receivedBytes);

    public long RtcpPackets => Interlocked.Read(ref rtcpPackets);

    public long LostPackets => reorder.LostPackets;

    public ReceptionStatus? ReceptionStatus { get; private set; }

    public Exception? RtcpFailure { get; private set; }

    public Exception? Failure { get; private set; }

    public bool IsRunning => !receiveTask.IsCompleted && Failure is null;

    public event EventHandler<TransportDataEventArgs>? TransportReceived;

    public static async Task<SatIpSession> OpenAsync(Uri streamUri, int firstPort = 50000, int lastPort = 50127, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streamUri);
        if (!streamUri.IsAbsoluteUri || !string.Equals(streamUri.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("An absolute RTSP stream URL is required.", nameof(streamUri));
        }

        if (firstPort < 1024 || lastPort > 65535 || firstPort >= lastPort)
        {
            throw new ArgumentOutOfRangeException(nameof(firstPort));
        }

        streamUri = SatIpTuning.WithPids(streamUri, SatIpTuning.Pids(streamUri));
        var (newRtp, newRtcp) = BindPair(firstPort, lastPort);
        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        RtspConnection? newConnection = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(streamUri.Host, streamUri.IsDefaultPort ? 554 : streamUri.Port, timeout.Token).ConfigureAwait(false);
            newConnection = new RtspConnection(tcp);
            var localPort = ((IPEndPoint)newRtp.Client.LocalEndPoint!).Port;
            var setup = await newConnection.SendAsync("SETUP", streamUri, transport: string.Create(CultureInfo.InvariantCulture, $"RTP/AVP;unicast;client_port={localPort}-{localPort + 1}"), cancellationToken: timeout.Token).ConfigureAwait(false);
            setup.EnsureSuccess();
            if (!setup.Headers.TryGetValue("Session", out var sessionHeader) || !setup.Headers.TryGetValue("com.ses.streamID", out var streamId))
            {
                throw new InvalidDataException("The SAT>IP SETUP response has no session or stream ID.");
            }

            var newSessionId = sessionHeader.Split(';')[0];
            var newSessionUri = new UriBuilder(streamUri) { Path = $"/stream={Uri.EscapeDataString(streamId)}", Query = string.Empty }.Uri;
            var play = await newConnection.SendAsync("PLAY", newSessionUri, newSessionId, cancellationToken: timeout.Token).ConfigureAwait(false);
            play.EnsureSuccess();
            var keepalive = TimeSpan.FromSeconds(25);
            foreach (var value in sessionHeader.Split(';', StringSplitOptions.TrimEntries))
            {
                if (value.StartsWith("timeout=", StringComparison.OrdinalIgnoreCase) && int.TryParse(value.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                {
                    keepalive = TimeSpan.FromSeconds(Math.Clamp(seconds / 2d, 1, 25));
                }
            }

            return new SatIpSession(newRtp, newRtcp, newConnection, newSessionUri, newSessionId, keepalive);
        }
        catch (Exception exception)
        {
            await CleanupFailedOpenAsync(newConnection, tcp, newRtp, newRtcp).ConfigureAwait(false);
            if (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new IOException("The SAT>IP connection timed out.", exception);
            }

            if (exception is SocketException)
            {
                throw new IOException("The SAT>IP server could not be reached.", exception);
            }

            throw;
        }
    }

    private static async Task CleanupFailedOpenAsync(RtspConnection? failedConnection, TcpClient failedClient, UdpClient failedRtp, UdpClient failedRtcp)
    {
        if (failedConnection is not null)
        {
            await failedConnection.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            failedClient.Dispose();
        }

        failedRtp.Dispose();
        failedRtcp.Dispose();
    }

    public TransportSubscription Subscribe(int capacity = 2048, bool discardOldest = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(capacity)
        {
            FullMode = discardOldest ? BoundedChannelFullMode.DropOldest : BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
        var id = Guid.NewGuid();
        lock (consumerLock)
        {
            consumers.Add(id, channel);
        }

        return new TransportSubscription(channel.Reader, () =>
        {
            lock (consumerLock)
            {
                if (consumers.Remove(id, out var removed))
                {
                    removed.Writer.TryComplete();
                }
            }
        });
    }

    public async Task UpdatePidsAsync(IEnumerable<int> add, IEnumerable<int> remove, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(remove);
        var query = new List<string>();
        Add("addpids", add);
        Add("delpids", remove);
        if (query.Count == 0)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var target = new UriBuilder(sessionUri) { Query = string.Join('&', query) }.Uri;
        var response = await connection.SendAsync("PLAY", target, sessionId, cancellationToken: timeout.Token).ConfigureAwait(false);
        response.EnsureSuccess();

        void Add(string name, IEnumerable<int> pids)
        {
            var values = pids.Distinct().Order().ToArray();
            if (values.Any(value => value is < 0 or > 8191))
            {
                throw new ArgumentOutOfRangeException(nameof(add));
            }

            if (values.Length > 0)
            {
                query.Add(name + "=" + string.Join(',', values.Select(value => value.ToString(CultureInfo.InvariantCulture))));
            }
        }
    }

    internal void Abort(Exception reason)
    {
        Failure = reason;
        CompleteConsumers(reason);
        lifetime.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(receiveTask, controlTask, rtcpTask, watchdogTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await connection.SendAsync("TEARDOWN", sessionUri, sessionId, cancellationToken: timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or SocketException or OperationCanceledException)
        {
            // The transport may already have failed; all local resources still need releasing.
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        rtp.Dispose();
        rtcp.Dispose();
        lifetime.Dispose();
        CompleteConsumers(null);
    }

    private static (UdpClient Rtp, UdpClient Rtcp) BindPair(int firstPort, int lastPort)
    {
        for (var port = firstPort + (firstPort & 1); port < lastPort; port += 2)
        {
            UdpClient? candidateRtp = null;
            try
            {
                candidateRtp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                candidateRtp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
                var candidateRtcp = new UdpClient(new IPEndPoint(IPAddress.Any, port + 1));
                return (candidateRtp, candidateRtcp);
            }
            catch (SocketException)
            {
                candidateRtp?.Dispose();
            }
        }

        throw new IOException("No free RTP/RTCP port pair is available in the configured range.");
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var datagram = await rtp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (!RtpPacket.TryParse(datagram.Buffer, out var packet) || packet is null)
                {
                    continue;
                }

                Interlocked.Exchange(ref lastPacketAt, TimeProvider.System.GetTimestamp());
                Interlocked.Increment(ref receivedPackets);
                Interlocked.Add(ref receivedBytes, packet.Payload.Length);
                foreach (var ready in reorder.Push(packet))
                {
                    TransportReceived?.Invoke(this, new TransportDataEventArgs(ready.Payload));
                    lock (consumerLock)
                    {
                        foreach (var consumer in consumers.Values)
                        {
                            if (!consumer.Writer.TryWrite(ready.Payload))
                            {
                                consumer.Writer.TryComplete(new IOException("The transport consumer could not keep up; capture was stopped rather than silently dropping packets."));
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or SocketException or InvalidOperationException)
        {
            Failure = exception;
            CompleteConsumers(exception);
        }
    }

    private async Task KeepAliveAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(keepaliveInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var response = await connection.SendAsync("OPTIONS", sessionUri, sessionId, cancellationToken: timeout.Token).ConfigureAwait(false);
                response.EnsureSuccess();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or SocketException or OperationCanceledException)
        {
            Failure = exception;
            CompleteConsumers(exception);
        }
    }

    private async Task ReceiveRtcpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var datagram = await rtcp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (RtcpAnnouncementParser.TryParse(datagram.Buffer, out var status))
                {
                    Interlocked.Increment(ref rtcpPackets);
                    ReceptionStatus = status ?? ReceptionStatus;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException exception)
        {
            RtcpFailure = exception;
        }
    }

    private async Task WatchReceptionAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (TimeProvider.System.GetElapsedTime(Interlocked.Read(ref lastPacketAt)) < TimeSpan.FromSeconds(10))
                {
                    continue;
                }

                Failure = new IOException("No valid MPEG transport packets have arrived for ten seconds.");
                CompleteConsumers(Failure);
                await lifetime.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void CompleteConsumers(Exception? exception)
    {
        lock (consumerLock)
        {
            foreach (var consumer in consumers.Values)
            {
                consumer.Writer.TryComplete(exception);
            }
        }
    }
}

