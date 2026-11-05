using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace FritzTV.Desktop.Services;

public sealed class SingleInstanceActivation : IAsyncDisposable
{
    private readonly InstanceOwnership ownership;
    private readonly NamedPipeServerStream server;
    private readonly CancellationTokenSource lifetime = new();
    private Task? listener;
    private int disposed;

    private SingleInstanceActivation(InstanceOwnership ownership, string pipeName)
    {
        this.ownership = ownership;
        server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public event EventHandler? ActivationRequested;

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
        listener ??= Task.Run(() => ListenAsync(lifetime.Token));
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
        }
        finally
        {
            server.Dispose();
            ownership.Dispose();
            lifetime.Dispose();
        }
    }

    private static async Task ActivateAsync(string name, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await client.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
            await client.FlushAsync(timeout.Token).ConfigureAwait(false);
            var acknowledgment = new byte[1];
            await client.ReadExactlyAsync(acknowledgment, timeout.Token).ConfigureAwait(false);
            if (acknowledgment[0] != 1)
            {
                throw new IOException("The existing application rejected the activation request.");
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The existing application did not respond to the activation request.", exception);
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                request.CancelAfter(TimeSpan.FromSeconds(5));
                var command = new byte[1];
                await server.ReadExactlyAsync(command, request.Token).ConfigureAwait(false);
                if (command[0] == 1)
                {
                    ActivationRequested?.Invoke(this, EventArgs.Empty);
                    await server.WriteAsync(new byte[] { 1 }, request.Token).ConfigureAwait(false);
                    await server.FlushAsync(request.Token).ConfigureAwait(false);
                }
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
                if (server.IsConnected)
                {
                    server.Disconnect();
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
