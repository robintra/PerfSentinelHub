using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Collection;

public sealed class PollWorker(
    SourcePoller poller,
    IOptions<HubOptions> options,
    TimeProvider timeProvider) : BackgroundService
{
    private readonly HubOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var concurrency = new SemaphoreSlim(_options.MaxConcurrentPolls);
        await Task.WhenAll(_options.Sources.Select(source =>
            RunSourceAsync(source, concurrency, stoppingToken)));
    }

    private async Task RunSourceAsync(
        SourceOptions source,
        SemaphoreSlim concurrency,
        CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                await concurrency.WaitAsync(cancellationToken);
                try
                {
                    await poller.PollAsync(source, cancellationToken);
                }
                finally
                {
                    concurrency.Release();
                }
                failures = 0;
                delay = _options.PollInterval;
            }
            catch (SourcePollException)
            {
                failures++;
                delay = Backoff.Delay(failures, Random.Shared.NextDouble());
            }

            await Task.Delay(delay, timeProvider, cancellationToken);
        }
    }
}
