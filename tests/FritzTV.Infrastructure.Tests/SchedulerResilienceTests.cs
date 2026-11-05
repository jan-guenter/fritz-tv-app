using System.Collections.Concurrent;
using System.Text.Json;
using FritzTV.Core.Models;
using FritzTV.Infrastructure.Recording;
using FritzTV.Infrastructure.Scheduling;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class SchedulerResilienceTests
{
    [Theory]
    [InlineData("timezone")]
    [InlineData("argument")]
    [InlineData("json")]
    [InlineData("epg-null-channel")]
    public async Task InvalidPersistedDefinitionCannotBlockAnUnrelatedDueRecording(string failure)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var logger = new FailureLogger();
        await using var scope = new RecordingTestScope(logger);
        var channel = await scope.InitializeAsync(cancellationToken);
        var now = scope.Clock.GetUtcNow();
        var invalid = new RecordingDefinition(Guid.NewGuid(), channel.Id, "Invalid persisted timer", now.AddDays(1), now.AddDays(1).AddHours(1),
            string.Equals(failure, "timezone", StringComparison.Ordinal) ? "FritzTV.DefinitelyMissingZone" : "UTC", RepeatOn: Weekdays.Monday,
            StartPadding: string.Equals(failure, "argument", StringComparison.Ordinal) ? TimeSpan.FromMinutes(-1) : TimeSpan.Zero, EndPadding: TimeSpan.Zero);
        await scope.Repository.SaveDefinitionAsync(invalid, cancellationToken);
        if (failure is "json" or "epg-null-channel")
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(scope.DirectoryPath, "app.db") }.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE definitions SET payload=$payload WHERE id=$id";
            command.Parameters.AddWithValue("$payload", string.Equals(failure, "json", StringComparison.Ordinal) ? "{" : JsonSerializer.Serialize(invalid with
            {
                ChannelId = null!,
                EventId = 1,
                RepeatOn = Weekdays.None,
            }));
            command.Parameters.AddWithValue("$id", invalid.Id.ToString());
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await scope.Repository.SaveRecordingAsync(new RecordingInstance(Guid.NewGuid(), Guid.Empty, channel.Id, "Valid due recording", now, now.AddHours(1), now, RecordingState.Scheduled), cancellationToken);
        using var service = new RecordingSchedulerService(scope.Coordinator, logger);
        await service.StartAsync(cancellationToken);
        try
        {
            Exception? reported = await logger.Failure.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.True(reported is null or TimeZoneNotFoundException or ArgumentException or JsonException);
            Assert.False(service.ExecuteTask!.IsCompleted);
            Assert.Contains(logger.Messages, message => message.Contains(invalid.Id.ToString(), StringComparison.Ordinal));
            await scope.Inputs.HealthyOpened.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await scope.Inputs.Latest[channel.Id].Written.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(service.ExecuteTask.IsCompleted);
            var active = Assert.Single(await scope.Coordinator.GetRecordingsAsync(cancellationToken));
            Assert.Equal(RecordingState.Recording, active.State);
            using var persisted = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(scope.DirectoryPath, "app.db") }.ConnectionString);
            await persisted.OpenAsync(cancellationToken);
            using var count = persisted.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM definitions WHERE id=$id";
            count.Parameters.AddWithValue("$id", invalid.Id.ToString());
            Assert.Equal(1L, await count.ExecuteScalarAsync(cancellationToken));
            await scope.Coordinator.StopAsync(active.Id, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }
    }

    private sealed class FailureLogger : ILogger<RecordingSchedulerService>, ILogger<RecordingCoordinator>
    {
        public TaskCompletionSource<Exception?> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Enqueue(formatter(state, exception));
            if (logLevel >= LogLevel.Warning)
            {
                Failure.TrySetResult(exception);
            }
        }
    }
}
