using System.Collections.Concurrent;
using FritzTV.Core.Models;
using FritzTV.Infrastructure.Recording;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FritzTV.Infrastructure.Tests;

public sealed class RecordingWarningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedInvalidTimerWarnsOnceUntilRepairOrDeletion(bool deleteToRepair)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var logger = new WarningLogger();
        await using var scope = new RecordingTestScope(logger);
        var channel = await scope.InitializeAsync(cancellationToken);
        var start = scope.Clock.GetUtcNow().AddDays(7);
        var definition = new RecordingDefinition(Guid.NewGuid(), channel.Id, "Future timer", start, start.AddHours(1), "UTC", StartPadding: TimeSpan.Zero, EndPadding: TimeSpan.Zero);
        await scope.Repository.SaveDefinitionAsync(definition, cancellationToken);
        await CorruptAsync(scope, definition.Id, cancellationToken);
        await scope.Coordinator.RunDueAsync(cancellationToken);
        for (var tick = 0; tick < 4; tick++)
        {
            scope.Clock.Advance(TimeSpan.FromSeconds(31));
            await scope.Coordinator.RunDueAsync(cancellationToken);
        }

        Assert.Single(logger.Messages, message => message.Contains(definition.Id.ToString(), StringComparison.Ordinal));
        if (deleteToRepair)
        {
            await scope.Repository.DeleteDefinitionAsync(definition.Id, cancellationToken);
        }
        else
        {
            await scope.Repository.SaveDefinitionAsync(definition, cancellationToken);
        }

        await scope.Coordinator.RunDueAsync(cancellationToken);
        await scope.Repository.SaveDefinitionAsync(definition, cancellationToken);
        await CorruptAsync(scope, definition.Id, cancellationToken);
        scope.Clock.Advance(TimeSpan.FromSeconds(31));
        await scope.Coordinator.RunDueAsync(cancellationToken);
        Assert.Equal(2, logger.Messages.Count(message => message.Contains(definition.Id.ToString(), StringComparison.Ordinal)));
    }

    private static async Task CorruptAsync(RecordingTestScope scope, Guid id, CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(scope.DirectoryPath, "app.db") }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE definitions SET payload='{' WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class WarningLogger : ILogger<RecordingCoordinator>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Messages.Enqueue(formatter(state, exception));
            }
        }
    }
}
