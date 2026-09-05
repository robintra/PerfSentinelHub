using Microsoft.Data.Sqlite;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Collection;

public sealed record PollResult(
    int ImportedCount,
    int RejectedCount,
    string ProducerVersion,
    bool IsPossiblyTruncated,
    // Null when the daemon publishes no incidents, or refused or failed the read.
    int? IncidentCount);

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
    // Ten daemon pages. The daemon's ring defaults to 200, so this stops a
    // misconfigured daemon rather than bounding anything a fleet should reach.
    internal const int IncidentsCap = 1000;

    public async Task<PollResult> PollAsync(
        SourceOptions source,
        CancellationToken cancellationToken)
    {
        var observedAtMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await database.MarkSourceAttemptAsync(source.Id, observedAtMs, cancellationToken);

        try
        {
            var status = await client.FetchStatusAsync(source, cancellationToken);
            var payload = await client.FetchFindingsAsync(source, cancellationToken);
            var batch = FindingParser.Parse(payload);
            await database.UpsertBatchAsync(
                new SourceSnapshot(source.Id, source.Name, source.Environment, status.Version),
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
            var incidentCount = await CollectIncidentsAsync(source, observedAtMs, cancellationToken);
            return new PollResult(
                batch.Findings.Count,
                batch.RejectedCount,
                status.Version,
                isPossiblyTruncated,
                incidentCount);
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

    /// <summary>
    ///     The incidents leg, after the findings are stored. Its failures are its
    ///     own: a wrong read key or a missing route says nothing about whether the
    ///     daemon answers, and source_state.unreachable_since_ms would demote
    ///     every finding this daemon reported. So nothing here reaches
    ///     MarkSourceFailureAsync, the outcome is filed in incident_reads. A
    ///     storage failure is the Hub's own and still fails the poll.
    /// </summary>
    private async Task<int?> CollectIncidentsAsync(
        SourceOptions source,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        try
        {
            var incidents = await ReadIncidentPagesAsync(source, cancellationToken);
            if (incidents is null)
            {
                await database.RecordIncidentReadAsync(
                    source.Id, observedAtMs, IncidentReadStates.Absent, null, cancellationToken);
                return null;
            }

            await database.UpsertIncidentsAsync(source.Id, incidents, observedAtMs, cancellationToken);
            return incidents.Count;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IncidentsUnauthorizedException)
        {
            await database.RecordIncidentReadAsync(
                source.Id, observedAtMs, IncidentReadStates.Unauthorized, null, cancellationToken);
            LogIncidentsUnauthorized(logger, source.Id);
            return null;
        }
        catch (Exception exception) when (exception is not SqliteException)
        {
            var errorCode = ErrorCode(exception);
            await database.RecordIncidentReadAsync(
                source.Id, observedAtMs, IncidentReadStates.Error, errorCode, cancellationToken);
            LogIncidentsFailed(logger, source.Id, errorCode);
            return null;
        }
    }

    // Null when the first page says the daemon has no incidents surface at all.
    private async Task<List<ParsedIncident>?> ReadIncidentPagesAsync(
        SourceOptions source,
        CancellationToken cancellationToken)
    {
        var incidents = new List<ParsedIncident>();
        for (var offset = 0; offset < IncidentsCap; offset += DaemonClient.IncidentsPageSize)
        {
            var payload = await client.FetchIncidentsPageAsync(source, offset, cancellationToken);
            if (payload is null)
                return offset == 0 ? null : incidents;

            var page = IncidentParser.Parse(payload);
            incidents.AddRange(page.Incidents);
            if (page.RejectedCount > 0)
                LogRejectedIncidents(logger, source.Id, page.RejectedCount);
            if (page.Incidents.Count + page.RejectedCount < DaemonClient.IncidentsPageSize)
                return incidents;
        }

        LogIncidentsCapped(logger, source.Id, IncidentsCap);
        return incidents;
    }

    // Shared with the daemon view: one classification for both readers.
    internal static string ErrorCode(Exception exception)
    {
        return exception switch
        {
            DaemonTimeoutException => "timeout",
            ResponseTooLargeException => "response_too_large",
            InvalidStatusException => "invalid_status",
            InvalidDataException => "invalid_findings",
            SqliteException => "storage_error",
            HttpRequestException httpException => httpException.StatusCode is null ? "network_error" : "http_error",
            _ => "network_error"
        };
    }

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

    [LoggerMessage(1104, LogLevel.Warning, "Source {SourceId} rejected {RejectedCount} incidents.")]
    private static partial void LogRejectedIncidents(ILogger logger, string sourceId, int rejectedCount);

    [LoggerMessage(
        1105,
        LogLevel.Warning,
        "Source {SourceId} refused the Hub's key on /api/incidents; its findings were still collected.")]
    private static partial void LogIncidentsUnauthorized(ILogger logger, string sourceId);

    [LoggerMessage(
        1106,
        LogLevel.Warning,
        "Source {SourceId} incidents read failed: {ErrorCode}; its findings were still collected.")]
    private static partial void LogIncidentsFailed(ILogger logger, string sourceId, string errorCode);

    [LoggerMessage(
        1107,
        LogLevel.Warning,
        "Source {SourceId} lists more than {Cap} incidents; the Hub stopped paging there.")]
    private static partial void LogIncidentsCapped(ILogger logger, string sourceId, int cap);
}
