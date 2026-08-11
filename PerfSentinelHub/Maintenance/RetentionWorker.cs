using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Maintenance;

public sealed partial class RetentionWorker(
    HubDatabase database,
    IOptions<HubOptions> options,
    TimeProvider timeProvider,
    ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var cutoff = (timeProvider.GetUtcNow() - options.Value.Retention).ToUnixTimeMilliseconds();
            try
            {
                await database.PurgeAsync(cutoff, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A failed purge must skip one pass, not stop the host and lose the read endpoint.
                LogPurgeFailure(logger, exception);
            }

            await Task.Delay(TimeSpan.FromDays(1), timeProvider, stoppingToken);
        }
    }

    [LoggerMessage(1201, LogLevel.Error, "Retention purge failed; retrying at the next pass.")]
    private static partial void LogPurgeFailure(ILogger logger, Exception exception);
}
