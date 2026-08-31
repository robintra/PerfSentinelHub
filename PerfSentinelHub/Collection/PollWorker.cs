using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Collection;

public sealed partial class PollWorker(
    SourcePoller poller,
    IOptions<HubOptions> options,
    TimeProvider timeProvider,
    ILogger<PollWorker> logger) : BackgroundService
{
    private readonly HubOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var concurrency = new SemaphoreSlim(_options.MaxConcurrentPolls);
        var tasks = new List<Task>(_options.Sources.Count);
        // A query would capture the disposable semaphore and obscure its lifetime.
        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var source in _options.Sources)
        {
            // A trace backend serves no findings endpoint. Polling one would
            // mark a healthy source unreachable on every interval.
            if (source.Kind != SourceKinds.Daemon)
                continue;
            tasks.Add(RunSourceAsync(source, concurrency, stoppingToken));
        }

        await Task.WhenAll(tasks);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A poll must never take the host down: SourcePoller writes to storage outside its
                // own guarded region, so anything can surface here, not only SourcePollException.
                if (exception is not SourcePollException)
                    LogUnexpectedPollFailure(logger, exception, source.Id);
                failures++;
                delay = Backoff.Delay(failures, Random.Shared.NextDouble());
            }

            await Task.Delay(delay, timeProvider, cancellationToken);
        }
    }

    [LoggerMessage(1001, LogLevel.Error, "Source {SourceId} poll failed unexpectedly.")]
    private static partial void LogUnexpectedPollFailure(
        ILogger logger,
        Exception exception,
        string sourceId);
}
