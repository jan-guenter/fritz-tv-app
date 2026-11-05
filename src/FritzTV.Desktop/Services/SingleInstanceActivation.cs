using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace FritzTV.Desktop.Services;

public sealed class SingleInstanceActivation : IAsyncDisposable
{
    private readonly InstanceOwnership ownership;
    private NamedPipeServerStream server;
    private Task connectionReady;
    private readonly CancellationTokenSource lifetime = new();
    private Task? listener;
    private int disposed;

    private SingleInstanceActivation(InstanceOwnership ownership, string pipeName)
    {
        this.ownership = ownership;
        PipeName = pipeName;
        server = CreateServer(pipeName);
        // Windows clients need a pending connection even while the desktop host is starting.
        connectionReady = server.WaitForConnectionAsync(lifetime.Token);
    }

    public event EventHandler? ActivationRequested;

    public string PipeName { get; }

    public static async Task<SingleInstanceActivation?> CreateOrActivateAsync(string applicationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        string identity = Environment.UserName + "\n" + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\n" + applicationId;
        string name = "fritztv-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var lease = new InstanceOwnership((OperatingSystem.IsWindows() ? "Global\\" : string.Empty) + name);
        try
        {
            if (await lease.Acquired.WaitAsync(cancellationToken).ConfigureAwait(false))
            {
                return new SingleInstanceActivation(lease, name);
            }

            await ActivateAsync(name, cancellationToken).ConfigureAwait(false);
            lease.Dispose();
            return null;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public void StartListening()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        listener ??= ListenAsync(lifetime.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            if (listener is not null)
            {
                await listener.ConfigureAwait(false);
            }
            else
            {
                try
                {
                    await connectionReady.ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException)
                {
                    // The protocol loop never started; its pending accept still needs observing.
                }
            }
        }
        finally
        {
            server.Dispose();
            ownership.Dispose();
            lifetime.Dispose();
        }
    }

    // A zero-sized Windows pipe blocks a client's first byte until the desktop starts reading.
    private static NamedPipeServerStream CreateServer(string name) => new(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 256, 256);

    private static async Task ActivateAsync(string name, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                try
                {
                    await SendActivationAsync(name, timeout.Token).ConfigureAwait(false);
                    return;
                }
                catch (Exception exception) when (exception is IOException or TimeoutException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The existing application did not respond to the activation request.", exception);
        }
    }

    private static async Task SendActivationAsync(string name, CancellationToken cancellationToken)
    {
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(250, cancellationToken).ConfigureAwait(false);
        await client.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
        await client.FlushAsync(cancellationToken).ConfigureAwait(false);
        var acknowledgment = new byte[1];
        await client.ReadExactlyAsync(acknowledgment, cancellationToken).ConfigureAwait(false);
        if (acknowledgment[0] != 1)
        {
            // A protocol rejection is terminal; only transport disconnects are retried.
            throw new InvalidDataException("The existing application rejected the activation request.");
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream connection = server;
            try
            {
                await connectionReady.ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                request.CancelAfter(TimeSpan.FromSeconds(5));
                var command = new byte[1];
                await connection.ReadExactlyAsync(command, request.Token).ConfigureAwait(false);
                if (command[0] == 1)
                {
                    ActivationRequested?.Invoke(this, EventArgs.Empty);
                }

                await connection.WriteAsync(new byte[] { command[0] == 1 ? (byte)1 : (byte)0 }, request.Token).ConfigureAwait(false);
                await connection.FlushAsync(request.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                // A disconnected or timed-out secondary must not stop future activation requests.
            }
            finally
            {
                try
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        // Keep the shared listener alive while replacing a disconnected instance.
                        server = CreateServer(PipeName);
                        connectionReady = server.WaitForConnectionAsync(cancellationToken);
                    }
                }
                finally
                {
                    connection.Dispose();
                }
            }
        }
    }

    private sealed class InstanceOwnership : IDisposable
    {
        private readonly ManualResetEventSlim release = new();
        private readonly TaskCompletionSource<bool> acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread thread;

        public InstanceOwnership(string name)
        {
            thread = new Thread(() => HoldMutex(name)) { IsBackground = true, Name = "FRITZ TV instance ownership" };
            thread.Start();
        }

        public Task<bool> Acquired => acquired.Task;

        public void Dispose()
        {
            release.Set();
            thread.Join();
            release.Dispose();
        }

        private void HoldMutex(string name)
        {
            try
            {
                using var mutex = new Mutex(initiallyOwned: false, name);
                bool owns;
                try
                {
                    owns = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    owns = true;
                }

                acquired.TrySetResult(owns);
                if (owns)
                {
                    release.Wait();
                    mutex.ReleaseMutex();
                }
            }
            catch (Exception exception)
            {
                acquired.TrySetException(exception);
            }
        }
    }
}
