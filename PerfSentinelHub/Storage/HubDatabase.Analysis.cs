using Microsoft.Data.Sqlite;

namespace PerfSentinelHub.Storage;

public sealed partial class HubDatabase
{
    private const string RunColumns = """
        id, status, source_id, source_name, environment, kind, request_json,
        requested_by, created_at_ms, started_at_ms, finished_at_ms,
        expires_at_ms, producer_version, error_code, result_json
        """;

    public async Task InsertRunAsync(AnalysisRun run, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO analysis_runs({RunColumns})
                VALUES ($id, $status, $source_id, $source_name, $environment, $kind,
                        $request_json, $requested_by, $created_at_ms, NULL, NULL,
                        NULL, NULL, NULL, NULL);
                """;
            command.Parameters.AddWithValue("$id", run.Id);
            command.Parameters.AddWithValue("$status", run.Status);
            command.Parameters.AddWithValue(SourceIdParameter, run.SourceId);
            command.Parameters.AddWithValue("$source_name", run.SourceName);
            command.Parameters.AddWithValue("$environment", run.Environment);
            command.Parameters.AddWithValue("$kind", run.Kind);
            command.Parameters.AddWithValue("$request_json", run.RequestJson);
            command.Parameters.AddWithValue("$requested_by", run.RequestedBy);
            command.Parameters.AddWithValue("$created_at_ms", run.CreatedAtMs);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Moves the oldest pending run to running and returns it, or null when
    /// nothing is queued. The write gate serialises this against every other
    /// writer, so two workers cannot claim the same row.
    /// </summary>
    public async Task<AnalysisRun?> TryClaimNextRunAsync(long startedAtMs, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                UPDATE analysis_runs
                SET status = '{AnalysisStatuses.Running}', started_at_ms = $started_at_ms
                WHERE id = (
                  SELECT id FROM analysis_runs
                  WHERE status = '{AnalysisStatuses.Pending}'
                  ORDER BY created_at_ms ASC, id ASC
                  LIMIT 1)
                RETURNING {RunColumns};
                """;
            command.Parameters.AddWithValue("$started_at_ms", startedAtMs);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadRun(reader) : null;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task CompleteRunAsync(
        string id,
        string status,
        long finishedAtMs,
        long? expiresAtMs,
        string? producerVersion,
        string? errorCode,
        string? resultJson,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE analysis_runs
                SET status = $status,
                    finished_at_ms = $finished_at_ms,
                    expires_at_ms = $expires_at_ms,
                    producer_version = $producer_version,
                    error_code = $error_code,
                    result_json = $result_json
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$finished_at_ms", finishedAtMs);
            command.Parameters.AddWithValue("$expires_at_ms", (object?)expiresAtMs ?? DBNull.Value);
            command.Parameters.AddWithValue("$producer_version", (object?)producerVersion ?? DBNull.Value);
            command.Parameters.AddWithValue("$error_code", (object?)errorCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$result_json", (object?)resultJson ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<AnalysisRun>> ListRunsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {RunColumns} FROM analysis_runs
            ORDER BY created_at_ms DESC, id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var runs = new List<AnalysisRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            runs.Add(ReadRun(reader));
        return runs;
    }

    public async Task<AnalysisRun?> FindRunAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RunColumns} FROM analysis_runs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRun(reader) : null;
    }

    public async Task<int> CountPendingRunsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*) FROM analysis_runs WHERE status = '{AnalysisStatuses.Pending}';";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), provider: null);
    }

    /// <summary>
    /// Marks every run left running by a previous process as interrupted. The
    /// Hub never replays one on its own: a silent retry would fire a second
    /// heavy query at a backend nobody asked to query twice.
    /// </summary>
    public async Task<int> InterruptRunningRunsAsync(long finishedAtMs, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                UPDATE analysis_runs
                SET status = '{AnalysisStatuses.Interrupted}', finished_at_ms = $finished_at_ms
                WHERE status IN ('{AnalysisStatuses.Running}', '{AnalysisStatuses.Pending}');
                """;
            command.Parameters.AddWithValue("$finished_at_ms", finishedAtMs);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Expires succeeded runs whose report is past its lifetime and returns
    /// their ids so the caller can delete the files. The row stays, holding
    /// its parameters: the most common next action is to run the same thing
    /// again.
    /// </summary>
    public async Task<IReadOnlyList<string>> ExpireRunsAsync(long nowMs, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                UPDATE analysis_runs
                SET status = '{AnalysisStatuses.Expired}'
                WHERE status = '{AnalysisStatuses.Succeeded}'
                  AND expires_at_ms IS NOT NULL
                  AND expires_at_ms <= $now_ms
                RETURNING id;
                """;
            command.Parameters.AddWithValue("$now_ms", nowMs);

            var ids = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ids.Add(reader.GetString(0));
            return ids;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static AnalysisRun ReadRun(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetInt64(8),
        reader.IsDBNull(9) ? null : reader.GetInt64(9),
        reader.IsDBNull(10) ? null : reader.GetInt64(10),
        reader.IsDBNull(11) ? null : reader.GetInt64(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetString(14));
}
