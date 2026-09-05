using Microsoft.Data.Sqlite;
using PerfSentinelHub.Api;
using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Storage;

public sealed partial class HubDatabase
{
    private const string IncidentColumns = """
                                           id, source_id, service, kind, at_ms, ended_at_ms, window_from_ms,
                                           window_to_ms, oldest_finding_ms, finding_count, incident_json,
                                           first_seen_ms, last_seen_ms
                                           """;

    /// <summary>
    ///     Stores one poll's incidents and files the read as ok, in one
    ///     transaction. On conflict the richer document wins: the daemon's own
    ///     record only grows, so a smaller one is a re-capture after a restart
    ///     against a ring that had already evicted the window, and it must not
    ///     replace the copy this table exists to keep. An end is kept once seen.
    /// </summary>
    public async Task UpsertIncidentsAsync(
        string sourceId,
        IReadOnlyList<ParsedIncident> incidents,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(false);
            foreach (var incident in incidents)
                await UpsertIncidentAsync(connection, transaction, sourceId, incident, observedAtMs, cancellationToken);
            await RecordIncidentReadAsync(
                connection, transaction, sourceId, observedAtMs, IncidentReadStates.Ok, null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task UpsertIncidentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceId,
        ParsedIncident incident,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
                               INSERT INTO incidents({IncidentColumns})
                               VALUES ($id, $source_id, $service, $kind, $at_ms, $ended_at_ms, $window_from_ms,
                                       $window_to_ms, $oldest_finding_ms, $finding_count, $incident_json,
                                       $observed_at, $observed_at)
                               ON CONFLICT(id) DO UPDATE SET
                                 source_id = CASE WHEN excluded.finding_count >= incidents.finding_count
                                   THEN excluded.source_id ELSE incidents.source_id END,
                                 oldest_finding_ms = CASE WHEN excluded.finding_count >= incidents.finding_count
                                   THEN excluded.oldest_finding_ms ELSE incidents.oldest_finding_ms END,
                                 incident_json = CASE WHEN excluded.finding_count >= incidents.finding_count
                                   THEN excluded.incident_json ELSE incidents.incident_json END,
                                 finding_count = MAX(incidents.finding_count, excluded.finding_count),
                                 ended_at_ms = COALESCE(excluded.ended_at_ms, incidents.ended_at_ms),
                                 last_seen_ms = excluded.last_seen_ms;
                               """;
        command.Parameters.AddWithValue("$id", incident.Id);
        command.Parameters.AddWithValue(SourceIdParameter, sourceId);
        command.Parameters.AddWithValue(ServiceParameter, incident.Service);
        command.Parameters.AddWithValue("$kind", incident.Kind);
        command.Parameters.AddWithValue("$at_ms", incident.AtMs);
        command.Parameters.AddWithValue("$ended_at_ms", (object?)incident.EndedAtMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$window_from_ms", incident.WindowFromMs);
        command.Parameters.AddWithValue("$window_to_ms", incident.WindowToMs);
        command.Parameters.AddWithValue("$oldest_finding_ms", (object?)incident.OldestFindingMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$finding_count", incident.FindingCount);
        command.Parameters.AddWithValue("$incident_json", incident.IncidentJson);
        command.Parameters.AddWithValue(ObservedAtParameter, observedAtMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     Files the outcome of an incidents read that stored nothing: the route
    ///     was absent, the key refused, or the read failed. Kept apart from
    ///     source_state on purpose, whose unreachable_since_ms feeds the finding
    ///     status and must only ever say whether the daemon answered at all.
    /// </summary>
    public async Task RecordIncidentReadAsync(
        string sourceId,
        long readAtMs,
        string state,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await RecordIncidentReadAsync(connection, null, sourceId, readAtMs, state, errorCode, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task RecordIncidentReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sourceId,
        long readAtMs,
        string state,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              INSERT INTO incident_reads(source_id, last_read_ms, state, last_error_code)
                              VALUES ($source_id, $read_at, $state, $error_code)
                              ON CONFLICT(source_id) DO UPDATE SET
                                last_read_ms = excluded.last_read_ms,
                                state = excluded.state,
                                last_error_code = excluded.last_error_code;
                              """;
        command.Parameters.AddWithValue(SourceIdParameter, sourceId);
        command.Parameters.AddWithValue("$read_at", readAtMs);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$error_code", (object?)errorCode ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Newest first, the daemon's own order, with the id as the tie-break so two pages never overlap.</summary>
    public async Task<IReadOnlyList<StoredIncident>> ListIncidentsAsync(
        IncidentQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
                               SELECT {IncidentColumns} FROM incidents
                               WHERE ($service IS NULL OR service = $service)
                                 AND ($source_id IS NULL OR source_id = $source_id)
                               ORDER BY at_ms DESC, id ASC
                               LIMIT $limit OFFSET $offset;
                               """;
        command.Parameters.AddWithValue(ServiceParameter, (object?)query.Service ?? DBNull.Value);
        command.Parameters.AddWithValue(SourceIdParameter, (object?)query.SourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", query.Limit);
        command.Parameters.AddWithValue("$offset", query.Offset);

        var rows = new List<StoredIncident>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadIncident(reader));
        return rows;
    }

    public async Task<StoredIncident?> FindIncidentAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {IncidentColumns} FROM incidents WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadIncident(reader) : null;
    }

    /// <summary>
    ///     The last incidents read per source, keyed by source id. A source with
    ///     no row has never had its incidents read.
    /// </summary>
    public async Task<Dictionary<string, IncidentRead>> QueryIncidentReadsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_id, last_read_ms, state, last_error_code FROM incident_reads;";

        var reads = new Dictionary<string, IncidentRead>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            reads[reader.GetString(0)] = new IncidentRead(
                reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3));
        return reads;
    }

    private static StoredIncident ReadIncident(SqliteDataReader reader)
    {
        return new StoredIncident(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.GetInt32(9),
            reader.GetString(10),
            reader.GetInt64(11),
            reader.GetInt64(12));
    }
}
