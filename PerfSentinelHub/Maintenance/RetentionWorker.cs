using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Maintenance;

public sealed class RetentionWorker(
    HubDatabase database,
    IOptions<HubOptions> options,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var cutoff = (timeProvider.GetUtcNow() - options.Value.Retention).ToUnixTimeMilliseconds();
            await database.PurgeAsync(cutoff, stoppingToken);
            await Task.Delay(TimeSpan.FromDays(1), timeProvider, stoppingToken);
        }
    }
}
