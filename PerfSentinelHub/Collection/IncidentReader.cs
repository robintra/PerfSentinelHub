using Microsoft.Data.Sqlite;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Collection;

/// <summary>
///     One daemon's incidents ring, read and stored. Its own class because two
///     callers share it: the poll, which reads every daemon on its interval, and
///     the incidents screen, which reads them again when an operator opens it.
///     The two must not drift on how a refused key or an oversized page is filed.
/// </summary>
/// <remarks>
///     Its failures are its own: a wrong read key or a missing route says nothing
///     about whether the daemon answers, and source_state.unreachable_since_ms
///     would demote every finding this daemon reported. So nothing here reaches
///     MarkSourceFailureAsync, the outcome is filed in incident_reads. A storage
///     failure is the Hub's own and is left to the caller.
/// </remarks>
public sealed partial class IncidentReader(
    DaemonClient client,
    HubDatabase database,
    ILogger<IncidentReader> logger)
{
    // Ten daemon pages. The daemon's ring defaults to 200, so this stops a
    // misconfigured daemon rather than bounding anything a fleet should reach.
    public const int IncidentsCap = 1000;

    /// <summary>
    ///     Reads the daemon's ring and stores it, or files why it could not.
    ///     Returns the count stored, null when the daemon published none.
    /// </summary>
    public async Task<int?> ReadAsync(
        SourceOptions source,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        try
        {
            var incidents = await ReadPagesAsync(source, cancellationToken);
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
            // The shared switch names the findings leg for a malformed body, and
            // this leg has its own parser.
            var errorCode = exception is InvalidDataException
                ? "invalid_incidents"
                : SourcePoller.ErrorCode(exception);
            await database.RecordIncidentReadAsync(
                source.Id, observedAtMs, IncidentReadStates.Error, errorCode, cancellationToken);
            LogIncidentsFailed(logger, source.Id, errorCode);
            return null;
        }
    }

    // Null when the first page says the daemon has no incidents surface at all.
    // A page over the body cap is re-read at half the size from the same
    // offset: the daemon embeds up to a thousand findings per incident, so a
    // full page of a busy daemon never fits, while one incident always does.
    // At a single incident the overflow is the daemon's and is filed as such.
    private async Task<List<ParsedIncident>?> ReadPagesAsync(
        SourceOptions source,
        CancellationToken cancellationToken)
    {
        var incidents = new List<ParsedIncident>();
        var limit = DaemonClient.IncidentsPageSize;
        var offset = 0;
        while (offset < IncidentsCap)
        {
            byte[]? payload;
            try
            {
                payload = await client.FetchIncidentsPageAsync(source, offset, limit, cancellationToken);
            }
            catch (ResponseTooLargeException) when (limit > 1)
            {
                limit /= 2;
                continue;
            }

            if (payload is null)
                return offset == 0 ? null : incidents;

            var page = IncidentParser.Parse(payload);
            incidents.AddRange(page.Incidents);
            if (page.RejectedCount > 0)
                LogRejectedIncidents(logger, source.Id, page.RejectedCount);
            if (page.Incidents.Count + page.RejectedCount < limit)
                return incidents;
            offset += limit;
        }

        LogIncidentsCapped(logger, source.Id, IncidentsCap);
        return incidents;
    }

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
