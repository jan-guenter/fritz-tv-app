using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FritzTV.Infrastructure.Tests;

internal sealed class LoopbackRtspServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<Task> clients = [];
    private readonly Task acceptTask;
    private int setupCount;

    private readonly ConcurrentBag<TcpClient> connections = [];

    public LoopbackRtspServer(bool deferFirstSetup = false, int sessionTimeoutSeconds = 60, bool malformedKeepalive = false, bool missingSetupSession = false)
    {
        DeferFirstSetup = deferFirstSetup;
        SessionTimeoutSeconds = sessionTimeoutSeconds;
        MalformedKeepalive = malformedKeepalive;
        MissingSetupSession = missingSetupSession;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptTask = AcceptAsync(lifetime.Token);
    }

    public int Port { get; }

    public bool DeferFirstSetup { get; }

    public int SessionTimeoutSeconds { get; }

    public bool MalformedKeepalive { get; }

    public bool MissingSetupSession { get; }

    public TaskCompletionSource FirstSetup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentQueue<string> Requests { get; } = new();

    public ConcurrentBag<int> RtpPorts { get; } = [];

    public void CloseConnections()
    {
        foreach (var connection in connections)
        {
            connection.Dispose();
        }
    }

    public Task SendTransportAsync(int pid, ushort sequence, CancellationToken cancellationToken)
    {
        var transport = new byte[188];
        transport[0] = 0x47;
        transport[1] = (byte)(pid >> 8);
        transport[2] = (byte)pid;
        transport[3] = 0x10;
        return SendBatchAsync(transport, sequence, cancellationToken);
    }

    public async Task SendBatchAsync(ReadOnlyMemory<byte> transport, ushort sequence, CancellationToken cancellationToken)
    {
        var datagram = new byte[12 + transport.Length];
        datagram[0] = 0x80;
        datagram[1] = 33;
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), sequence);
        datagram[11] = 1;
        transport.CopyTo(datagram.AsMemory(12));
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        foreach (var port in RtpPorts)
        {
            await sender.SendAsync(datagram, new IPEndPoint(IPAddress.Loopback, port), cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        listener.Stop();
        await acceptTask.ConfigureAwait(false);
        await Task.WhenAll(clients).ConfigureAwait(false);
        lifetime.Dispose();
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                connections.Add(client);
                clients.Add(HandleAsync(client, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var ownedClient = client;
        try
        {
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                Requests.Enqueue(line);
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } header)
                {
                    var offset = header.IndexOf("client_port=", StringComparison.Ordinal);
                    if (offset >= 0)
                    {
                        RtpPorts.Add(int.Parse(header[(offset + 12)..].Split('-')[0], CultureInfo.InvariantCulture));
                    }
                }

                if (line.StartsWith("SETUP ", StringComparison.Ordinal))
                {
                    var current = Interlocked.Increment(ref setupCount);
                    FirstSetup.TrySetResult();
                    if (current == 1 && DeferFirstSetup)
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    }
                }

                var response = FormattableString.Invariant($"RTSP/1.0 200 OK\r\nCSeq: 0\r\nSession: test-session;timeout={SessionTimeoutSeconds}\r\ncom.ses.streamID: 1\r\nContent-Length: 0\r\n\r\n");
                if (MalformedKeepalive && line.StartsWith("OPTIONS ", StringComparison.Ordinal))
                {
                    response = "Synthetic malformed RTSP status\r\n";
                }
                else if (MissingSetupSession && line.StartsWith("SETUP ", StringComparison.Ordinal))
                {
                    response = "RTSP/1.0 200 OK\r\nCSeq: 0\r\nContent-Length: 0\r\n\r\n";
                }
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // A canceled tuning attempt deliberately closes its RTSP connection.
        }
    }
}
