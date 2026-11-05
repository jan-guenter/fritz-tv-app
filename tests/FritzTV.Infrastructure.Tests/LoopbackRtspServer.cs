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

    public LoopbackRtspServer(bool deferFirstSetup = false, int sessionTimeoutSeconds = 60, bool malformedKeepalive = false, bool missingSetupSession = false, bool blockPidResponse = false, bool echoSequence = false, bool wrongSequence = false, string? blockMethod = null)
    {
        DeferFirstSetup = deferFirstSetup;
        SessionTimeoutSeconds = sessionTimeoutSeconds;
        MalformedKeepalive = malformedKeepalive;
        MissingSetupSession = missingSetupSession;
        BlockPidResponse = blockPidResponse;
        EchoSequence = echoSequence;
        WrongSequence = wrongSequence;
        BlockMethod = blockMethod;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptTask = AcceptAsync(lifetime.Token);
    }

    public int Port { get; }

    public bool DeferFirstSetup { get; }

    public int SessionTimeoutSeconds { get; }

    public bool MalformedKeepalive { get; }

    public bool MissingSetupSession { get; }

    public bool BlockPidResponse { get; }

    public bool EchoSequence { get; }

    public bool WrongSequence { get; }

    public string? BlockMethod { get; }

    public TaskCompletionSource PidRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ReleasePidResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
                var requestSequence = await ReadHeadersAsync(reader, cancellationToken).ConfigureAwait(false);

                if (line.StartsWith("SETUP ", StringComparison.Ordinal))
                {
                    var current = Interlocked.Increment(ref setupCount);
                    FirstSetup.TrySetResult();
                    if (current == 1 && DeferFirstSetup)
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (BlockMethod is not null && line.StartsWith(BlockMethod + " ", StringComparison.Ordinal))
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }

                if (BlockPidResponse && (line.Contains("addpids=", StringComparison.Ordinal) || line.Contains("delpids=", StringComparison.Ordinal)))
                {
                    PidRequest.TrySetResult();
                    await ReleasePidResponse.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                var response = Response(line, requestSequence);
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // A canceled tuning attempt deliberately closes its RTSP connection.
        }
    }
    private async Task<string> ReadHeadersAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var sequence = "0";
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } header)
        {
            var offset = header.IndexOf("client_port=", StringComparison.Ordinal);
            if (offset >= 0)
            {
                RtpPorts.Add(int.Parse(header[(offset + 12)..].Split('-')[0], CultureInfo.InvariantCulture));
            }
            if (header.StartsWith("CSeq:", StringComparison.OrdinalIgnoreCase))
            {
                sequence = header[5..].Trim();
            }
        }
        return sequence;
    }

    private string Response(string request, string sequence)
    {
        if (MalformedKeepalive && request.StartsWith("OPTIONS ", StringComparison.Ordinal))
        {
            return "Synthetic malformed RTSP status\r\n";
        }
        if (MissingSetupSession && request.StartsWith("SETUP ", StringComparison.Ordinal))
        {
            return "RTSP/1.0 200 OK\r\nCSeq: 0\r\nContent-Length: 0\r\n\r\n";
        }
        var reply = WrongSequence ? "999" : EchoSequence ? sequence : "0";
        return FormattableString.Invariant($"RTSP/1.0 200 OK\r\nCSeq: {reply}\r\nSession: test-session;timeout={SessionTimeoutSeconds}\r\ncom.ses.streamID: 1\r\nContent-Length: 0\r\n\r\n");
    }

}
