using System.Text.Json;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Api;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Analysis;

/// <summary>
/// Drains the run queue. Nothing here retries: a run the service lost is
/// reported as interrupted and waits for a human, because a silent replay
/// would fire a second heavy query at a backend nobody asked to query twice.
/// </summary>
public sealed partial class AnalysisWorker(
    HubDatabase database,
    AnalysisRunner runner,
    IOptions<HubOptions> options,
    TimeProvider timeProvider,
    ILogger<AnalysisWorker> logger) : BackgroundService
{
    // Polled rather than signalled. A queue this shallow does not earn a
    // wake-up channel, and polling survives a restart with no state to rebuild.
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);
    // A report's lifetime is counted down in the interface, so the sweep has
    // to be finer than the lifetime itself or a dead link keeps answering.
    private static readonly TimeSpan ExpirySweepInterval = TimeSpan.FromMinutes(1);

    private readonly HubOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interrupted = await database.InterruptRunningRunsAsync(
            timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            stoppingToken);
        if (interrupted > 0)
            LogInterruptedOnStartup(logger, interrupted);

        var swept = runner.SweepScratchFiles();
        if (swept > 0)
            LogScratchSwept(logger, swept);

        var workers = Enumerable
            .Range(0, _options.Analysis.Workers)
            .Select(_ => DrainAsync(stoppingToken))
            .Append(SweepExpiredReportsAsync(stoppingToken));
        await Task.WhenAll(workers);
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await TryRunOneAsync(cancellationToken))
                    await Task.Delay(IdleDelay, timeProvider, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A broken run must never take the host down, and the loop must
                // not spin on a storage failure it cannot fix.
                LogDrainFailed(logger, exception);
                await Task.Delay(IdleDelay, timeProvider, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Deletes the reports whose lifetime ran out and marks their runs expired.
    /// The row itself stays, holding its parameters: the most common next
    /// action is to run the same thing again.
    /// </summary>
    private async Task SweepExpiredReportsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var expired = await database.ExpireRunsAsync(
                    timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    cancellationToken);
                foreach (var id in expired)
                    runner.DeleteReport(id);
                if (expired.Count > 0)
                    LogReportsExpired(logger, expired.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogSweepFailed(logger, exception);
            }

            await Task.Delay(ExpirySweepInterval, timeProvider, cancellationToken);
        }
    }

    private async Task<bool> TryRunOneAsync(CancellationToken cancellationToken)
    {
        var startedAtMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var run = await database.TryClaimNextRunAsync(startedAtMs, cancellationToken);
        if (run is null)
            return false;

        // Whatever happens, the row has to reach a terminal status: a throw
        // that escaped here would leave it running forever, polled by a screen
        // that never stops waiting.
        RunOutcome outcome;
        try
        {
            outcome = await ExecuteRunAsync(run, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRunCrashed(logger, exception, run.Id);
            // The only throw that reaches here is a stored request the Hub
            // cannot read, which is the request's fault and not the Hub's:
            // internal would tell the operator to retry an identical failure.
            outcome = new RunOutcome(AnalysisStatuses.Failed, AnalysisErrorCodes.InvalidRequest, null, null);
        }

        var finishedAtMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await database.CompleteRunAsync(
            run.Id,
            outcome.Status,
            finishedAtMs,
            outcome.Status == AnalysisStatuses.Succeeded
                ? finishedAtMs + (long)_options.Analysis.ReportRetention.TotalMilliseconds
                : null,
            outcome.ProducerVersion,
            outcome.ErrorCode,
            outcome.Summary is { } summary ? AnalysisRunWriter.SerializeSummary(summary) : null,
            cancellationToken);
        return true;
    }

    private async Task<RunOutcome> ExecuteRunAsync(AnalysisRun run, CancellationToken cancellationToken)
    {
        var source = _options.Sources.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, run.SourceId, StringComparison.Ordinal));
        if (source is null)
        {
            // The source was removed from the configuration between submission
            // and execution. The run keeps its own copy of the name, so the
            // card stays readable, but there is nothing left to query.
            LogSourceGone(logger, run.Id, run.SourceId);
            return new RunOutcome(AnalysisStatuses.Failed, AnalysisErrorCodes.InvalidRequest, null, null);
        }

        using var document = JsonDocument.Parse(run.RequestJson);
        var request = AnalysisRequest.TryParse(
            document.RootElement,
            source,
            _options.Analysis,
            timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            out _);
        // Re-validated against the source as it stands now: its kind may have
        // changed while the run waited, which changes what the request means.
        return request is null
            ? new RunOutcome(AnalysisStatuses.Failed, AnalysisErrorCodes.InvalidRequest, null, null)
            : await runner.RunAsync(run, source, request, cancellationToken);
    }

    [LoggerMessage(1301, LogLevel.Warning,
        "Marked {Count} run(s) interrupted: they were still running when the service stopped.")]
    private static partial void LogInterruptedOnStartup(ILogger logger, int count);

    [LoggerMessage(1302, LogLevel.Error, "The analysis queue drain failed unexpectedly.")]
    private static partial void LogDrainFailed(ILogger logger, Exception exception);

    [LoggerMessage(1303, LogLevel.Warning, "Run {RunId} targets source {SourceId}, which is no longer configured.")]
    private static partial void LogSourceGone(ILogger logger, string runId, string sourceId);

    [LoggerMessage(1304, LogLevel.Information, "Deleted {Count} expired report(s).")]
    private static partial void LogReportsExpired(ILogger logger, int count);

    [LoggerMessage(1305, LogLevel.Error, "The expired-report sweep failed, retrying at the next pass.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(1306, LogLevel.Error, "Run {RunId} threw outside the runner and was failed as invalid.")]
    private static partial void LogRunCrashed(ILogger logger, Exception exception, string runId);

    [LoggerMessage(1307, LogLevel.Information, "Removed {Count} scratch file(s) a previous process left behind.")]
    private static partial void LogScratchSwept(ILogger logger, int count);
}
