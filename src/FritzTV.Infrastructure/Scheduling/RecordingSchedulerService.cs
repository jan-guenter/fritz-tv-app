using FritzTV.Infrastructure.Recording;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FritzTV.Infrastructure.Scheduling;

public sealed class RecordingSchedulerService(RecordingCoordinator coordinator, ILogger<RecordingSchedulerService> logger, TimeProvider? timeProvider = null) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogTickFailure = LoggerMessage.Define(LogLevel.Error, new EventId(1, "SchedulerTickFailed"), "Recording scheduler tick failed; it will retry.");
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await coordinator.RunDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                LogTickFailure(logger, exception);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken).ConfigureAwait(false);
        }
    }
}
