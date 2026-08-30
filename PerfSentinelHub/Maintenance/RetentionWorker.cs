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
            var now = timeProvider.GetUtcNow();
            var cutoff = (now - options.Value.Retention).ToUnixTimeMilliseconds();
            // Finished runs age out on their own clock. Reusing Retention would
            // keep six months of run history for a table nothing reads back.
            var runCutoff = (now - options.Value.Analysis.RunRetention).ToUnixTimeMilliseconds();
            try
            {
                await database.PurgeAsync(cutoff, runCutoff, stoppingToken);
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
