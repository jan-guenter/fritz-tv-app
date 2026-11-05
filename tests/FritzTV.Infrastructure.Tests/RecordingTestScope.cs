using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FritzTV.Core.Exceptions;
using FritzTV.Core.Models;
using FritzTV.Infrastructure.Persistence;
using FritzTV.Infrastructure.Playback;
using FritzTV.Infrastructure.Power;
using FritzTV.Infrastructure.Recording;
using FritzTV.Infrastructure.Transport;
using Microsoft.Data.Sqlite;

namespace FritzTV.Infrastructure.Tests;

internal sealed class RecordingTestScope : IAsyncDisposable
{
    private readonly SatIpSessionManager sessions = new();

    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "fritztv-recordings-" + Guid.NewGuid().ToString("N"));

    public ManualTimeProvider Clock { get; } = new();

    public AppRepository Repository { get; }

    public TestInputFactory Inputs { get; } = new();

    public TestFinalizer Finalizer { get; } = new();

    public TestPower Power { get; } = new();

    public RecordingCoordinator Coordinator { get; }

    public RecordingTestScope()
    {
        Repository = new AppRepository(Path.Combine(DirectoryPath, "app.db"));
        Coordinator = new RecordingCoordinator(Repository, sessions, Clock, Inputs, Finalizer, Power);
    }

    public async Task<TvChannel> InitializeAsync(CancellationToken cancellationToken)
    {
        await Repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Repository.SaveSettingsAsync(new AppSettings { RecordingDirectory = DirectoryPath, FreeSpaceReserveBytes = 0 }, cancellationToken).ConfigureAwait(false);
        var channel = new TvChannel("synthetic", Guid.NewGuid(), "Synthetic / programme", ChannelKind.Television, new Uri("rtsp://192.0.2.1/?freq=100&pids=0,100,101"), ServiceId: 3);
        await Repository.SaveChannelsAsync([channel], cancellationToken).ConfigureAwait(false);
        return channel;
    }

    public async ValueTask DisposeAsync()
    {
        await Coordinator.DisposeAsync().ConfigureAwait(false);
        await sessions.DisposeAsync().ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    internal sealed class TestInputFactory : IRecordingInputFactory
    {
        private int active;
        private int opened;

        public int FailingInputs { get; set; }

        public TaskCompletionSource Reopened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Limit { get; set; } = int.MaxValue;

        public int Active => Volatile.Read(ref active);

        public ConcurrentQueue<string> Attempts { get; } = new();

        public ConcurrentDictionary<string, TestInput> Latest { get; } = new(StringComparer.Ordinal);

        public Task<IRecordingInput> OpenAsync(TvChannel channel, BufferedPlayback? playback = null, DateTimeOffset? from = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts.Enqueue(channel.Id);
            if (Interlocked.Increment(ref active) > Limit)
            {
                Interlocked.Decrement(ref active);
                throw new TunerUnavailableException("Synthetic tuner is reserved by an active recording.");
            }

            var index = Interlocked.Increment(ref opened);
            var input = new TestInput(() => Interlocked.Decrement(ref active), index <= FailingInputs);
            Latest[channel.Id] = input;
            if (index > 1)
            {
                Reopened.TrySetResult();
            }

            return Task.FromResult<IRecordingInput>(input);
        }
    }

    internal sealed class TestInput(Action release, bool failAfterWrite = false) : IRecordingInput
    {
        private int disposed;

        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int? ServiceId => 3;

        public long LostPackets { get; set; }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var data = new byte[188 * 512];
            for (var offset = 0; offset < data.Length; offset += 188)
            {
                data[offset] = 0x47;
                data[offset + 2] = 100;
                data[offset + 3] = 0x10;
            }

            yield return data;
            Written.TrySetResult();
            if (failAfterWrite)
            {
                throw new IOException("Synthetic transport interruption.");
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                release();
                Disposed.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }

    internal sealed class TestFinalizer : IRecordingFinalizer
    {
        public bool Fail { get; set; }

        public bool WaitForRelease { get; set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<bool> Retained { get; } = new();

        public async Task<RecordingFinalizationResult> FinalizeAsync(string sourcePath, string outputPath, TvChannel channel, bool retainSource, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            if (WaitForRelease)
            {
                await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (Fail)
            {
                throw new IOException("Synthetic remux failure.");
            }

            File.Copy(sourcePath, outputPath, overwrite: false);
            Retained.Enqueue(retainSource);
            if (!retainSource)
            {
                File.Delete(sourcePath);
            }

            return new RecordingFinalizationResult(outputPath, retainSource);
        }

        public Task ValidateAsync(string path, ChannelKind kind, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(path))
            {
                throw new InvalidDataException("Synthetic output is absent.");
            }

            return Task.CompletedTask;
        }
    }

    internal sealed class TestPower : IPowerInhibitor
    {
        private int active;

        public int Active => Volatile.Read(ref active);

        public ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref active);
            return ValueTask.FromResult<IAsyncDisposable>(new Token(() => Interlocked.Decrement(ref active)));
        }

        private sealed class Token(Action release) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                release();
                return ValueTask.CompletedTask;
            }
        }
    }
}
