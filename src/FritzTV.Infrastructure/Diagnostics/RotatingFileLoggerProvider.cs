using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace FritzTV.Infrastructure.Diagnostics;

public sealed class RotatingFileLoggerProvider : ILoggerProvider, IAsyncDisposable
{
    private readonly RotatingFileLoggerOptions options;
    private readonly TimeProvider clock;
    private readonly Channel<Entry> queue;
    private readonly Task writerTask;
    private FileStream? output;
    private long dropped;
    private Exception? lastError;
    private int disposed;

    public RotatingFileLoggerProvider(RotatingFileLoggerOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.DirectoryPath) || options.MaxFileBytes < 4096 || options.RetainedFileCount < 1 || options.QueueCapacity < 1)
        {
            throw new ArgumentException("A directory, a file size of at least 4096 bytes, and positive retention/queue counts are required.", nameof(options));
        }
        this.options = options with { DirectoryPath = Path.GetFullPath(options.DirectoryPath) };
        clock = timeProvider ?? TimeProvider.System;
        queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(options.QueueCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        writerTask = Task.Run(WriteLoopAsync);
    }

    public long DroppedMessages => Interlocked.Read(ref dropped);

    public Exception? LastError => Volatile.Read(ref lastError);

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(new Entry(null, completion), cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            queue.Writer.TryComplete();
        }

        await writerTask.ConfigureAwait(false);
    }

    private void Enqueue(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        int limit = (int)Math.Min(options.MaxFileBytes / 24, 16 * 1024);
        var record = new LogRecord(clock.GetUtcNow(), level.ToString(), Trim(category, 128), eventId.Id,
            Trim(LogRedactor.Redact(Trim(message, limit * 2)), limit), exception is null ? null : Trim(LogRedactor.Redact(Trim(exception.ToString(), limit * 2)), limit));
        if (!queue.Writer.TryWrite(new Entry(record, null)))
        {
            Interlocked.Increment(ref dropped);
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (Entry entry in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await WriteEntryAsync(entry).ConfigureAwait(false);
            }
        }
        finally
        {
            await CloseOutputAsync().ConfigureAwait(false);
        }
    }

    private async Task CloseOutputAsync()
    {
        FileStream? stream = output;
        output = null;
        if (stream is not null)
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Volatile.Write(ref lastError, exception);
                Interlocked.Increment(ref dropped);
            }
        }
    }

    private async Task WriteEntryAsync(Entry entry)
    {
        try
        {
            if (entry.Record is { } record)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record) + "\n");
                await EnsureOutputAsync(bytes.Length).ConfigureAwait(false);
                await output!.WriteAsync(bytes).ConfigureAwait(false);
            }
            else
            {
                if (LastError is { } error)
                {
                    throw new IOException("Local diagnostic writes failed; some messages could not be retained.", error);
                }

                if (output is not null)
                {
                    await output.FlushAsync().ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
            }

            entry.Completion?.TrySetResult();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Volatile.Write(ref lastError, exception);
            Interlocked.Increment(ref dropped);
            await CloseOutputAsync().ConfigureAwait(false);
            entry.Completion?.TrySetException(exception);
        }
    }

    private async Task EnsureOutputAsync(int incomingBytes)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(options.DirectoryPath);
        }
        else
        {
            Directory.CreateDirectory(options.DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        string path = Path.Combine(options.DirectoryPath, "fritz-tv.log");
        output ??= OpenFile(path);
        if (output.Length > 0 && output.Length + incomingBytes > options.MaxFileBytes)
        {
            await CloseOutputAsync().ConfigureAwait(false);
            for (int index = options.RetainedFileCount - 1; index >= 1; index--)
            {
                string previous = index == 1 ? path : path + "." + (index - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                string destination = path + "." + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (File.Exists(previous))
                {
                    File.Move(previous, destination, overwrite: true);
                }
            }

            if (options.RetainedFileCount == 1)
            {
                File.Delete(path);
            }

            output = OpenFile(path);
        }
    }

    private static FileStream OpenFile(string path)
    {
        var settings = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read | FileShare.Delete, BufferSize = 4096, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows())
        {
            settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, settings);
    }

    private static string Trim(string value, int maximum) => value.Length <= maximum ? value : value[..maximum] + "…";

    private sealed record Entry(LogRecord? Record, TaskCompletionSource? Completion);

    private sealed record LogRecord(DateTimeOffset Utc, string Level, string Category, int EventId, string Message, string? Exception);

    private sealed class FileLogger(RotatingFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                owner.Enqueue(category, logLevel, eventId, formatter(state, exception), exception);
            }
        }
    }
}
