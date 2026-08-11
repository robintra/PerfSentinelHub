using Microsoft.Data.Sqlite;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Collection;

public sealed record PollResult(
    int ImportedCount,
    int RejectedCount,
    string ProducerVersion,
    bool IsPossiblyTruncated);

public sealed class SourcePollException(string errorCode, Exception innerException)
    : Exception($"Source poll failed: {errorCode}", innerException)
{
    public string ErrorCode { get; } = errorCode;
}

public sealed partial class SourcePoller(
    DaemonClient client,
    HubDatabase database,
    TimeProvider timeProvider,
    ILogger<SourcePoller> logger)
{
    public async Task<PollResult> PollAsync(
        SourceOptions source,
        CancellationToken cancellationToken)
    {
        var observedAtMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await database.MarkSourceAttemptAsync(source.Id, observedAtMs, cancellationToken);

        try
        {
            var version = await client.FetchStatusAsync(source, cancellationToken);
            var payload = await client.FetchFindingsAsync(source, cancellationToken);
            var batch = FindingParser.Parse(payload);
            await database.UpsertBatchAsync(
                new SourceSnapshot(source.Id, source.Name, source.Environment, version),
                batch,
                observedAtMs,
                cancellationToken);
            if (batch.RejectedCount > 0)
                LogRejectedFindings(
                    logger,
                    source.Id,
                    batch.RejectedCount,
                    batch.RejectedCount + batch.Findings.Count);
            var isPossiblyTruncated = batch.Findings.Count + batch.RejectedCount == DaemonClient.FindingsLimit;
            if (isPossiblyTruncated)
                LogPossiblyTruncated(logger, source.Id);
            return new PollResult(batch.Findings.Count, batch.RejectedCount, version, isPossiblyTruncated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var errorCode = ErrorCode(exception);
            await database.MarkSourceFailureAsync(source.Id, observedAtMs, errorCode, cancellationToken);
            LogPollFailure(logger, source.Id, errorCode);
            throw new SourcePollException(errorCode, exception);
        }
    }

    private static string ErrorCode(Exception exception) => exception switch
    {
        DaemonTimeoutException => "timeout",
        ResponseTooLargeException => "response_too_large",
        InvalidStatusException => "invalid_status",
        InvalidDataException => "invalid_findings",
        SqliteException => "storage_error",
        HttpRequestException httpException => httpException.StatusCode is null ? "network_error" : "http_error",
        _ => "network_error"
    };

    [LoggerMessage(
        1101,
        LogLevel.Warning,
        "Source {SourceId} rejected {RejectedCount} of {ReceivedCount} findings.")]
    private static partial void LogRejectedFindings(
        ILogger logger,
        string sourceId,
        int rejectedCount,
        int receivedCount);

    [LoggerMessage(
        1102,
        LogLevel.Warning,
        "Source {SourceId} poll returned the daemon cap and is possibly truncated; Hub push export is required for complete high-volume coverage.")]
    private static partial void LogPossiblyTruncated(ILogger logger, string sourceId);

    [LoggerMessage(1103, LogLevel.Warning, "Source {SourceId} poll failed: {ErrorCode}")]
    private static partial void LogPollFailure(ILogger logger, string sourceId, string errorCode);
}
