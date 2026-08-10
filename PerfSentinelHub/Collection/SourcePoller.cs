using Microsoft.Data.Sqlite;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Collection;

public sealed record PollResult(int ImportedCount, int RejectedCount, string ProducerVersion);

public sealed class SourcePollException(string errorCode, Exception innerException)
    : Exception($"Source poll failed: {errorCode}", innerException)
{
    public string ErrorCode { get; } = errorCode;
}

public sealed class SourcePoller(
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
                logger.LogWarning(
                    "Source {SourceId} rejected {RejectedCount} of {ReceivedCount} findings.",
                    source.Id,
                    batch.RejectedCount,
                    batch.RejectedCount + batch.Findings.Count);
            return new PollResult(batch.Findings.Count, batch.RejectedCount, version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var errorCode = ErrorCode(exception);
            await database.MarkSourceFailureAsync(source.Id, observedAtMs, errorCode, cancellationToken);
            logger.LogWarning("Source {SourceId} poll failed: {ErrorCode}", source.Id, errorCode);
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
        HttpRequestException { StatusCode: not null } => "http_error",
        HttpRequestException => "network_error",
        _ => "network_error"
    };
}
