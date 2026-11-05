using System.Collections.Concurrent;
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

    public LoopbackRtspServer(bool deferFirstSetup = false)
    {
        DeferFirstSetup = deferFirstSetup;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptTask = AcceptAsync(lifetime.Token);
    }

    public int Port { get; }

    public bool DeferFirstSetup { get; }

    public TaskCompletionSource FirstSetup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentQueue<string> Requests { get; } = new();

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
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 })
                {
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

                var response = "RTSP/1.0 200 OK\r\nCSeq: 0\r\nSession: test-session;timeout=60\r\ncom.ses.streamID: 1\r\nContent-Length: 0\r\n\r\n";
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or SocketException)
        {
            // A canceled tuning attempt deliberately closes its RTSP connection.
        }
    }
}
