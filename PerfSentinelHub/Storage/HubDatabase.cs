using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Storage;

public sealed class HubDatabase(IOptions<HubOptions> options, TimeProvider timeProvider) : IDisposable
{
    private readonly string _databasePath = options.Value.DatabasePath;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private int _ready;

    public bool IsReady => Volatile.Read(ref _ready) == 1;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (IsReady)
                return;

            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using (var pragmas = connection.CreateCommand())
            {
                pragmas.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                await pragmas.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var transaction = connection.BeginTransaction(deferred: false);
            await using (var migration = connection.CreateCommand())
            {
                migration.Transaction = transaction;
                migration.CommandText = Schema.V1;
                await migration.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var version = connection.CreateCommand())
            {
                version.Transaction = transaction;
                version.CommandText = """
                    INSERT OR IGNORE INTO schema_migrations(version, applied_at_ms)
                    VALUES (1, $applied_at_ms);
                    """;
                version.Parameters.AddWithValue(
                    "$applied_at_ms",
                    timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
                await version.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            Volatile.Write(ref _ready, 1);
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString());

        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    public async Task UpsertBatchAsync(
        SourceSnapshot source,
        ParsedBatch batch,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);

        foreach (var finding in batch.Findings)
        {
            await UpsertFindingAsync(connection, transaction, finding, observedAtMs, cancellationToken);
            await UpsertFindingSourceAsync(connection, transaction, source, finding, observedAtMs, cancellationToken);
            await UpsertHeartbeatAsync(connection, transaction, source, finding, observedAtMs, cancellationToken);
        }

        await using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = """
                INSERT INTO source_state(
                  source_id, last_attempt_ms, last_success_ms, unreachable_since_ms,
                  producer_version, last_error_code)
                VALUES ($source_id, $observed_at, $observed_at, NULL, $producer_version, NULL)
                ON CONFLICT(source_id) DO UPDATE SET
                  last_attempt_ms = excluded.last_attempt_ms,
                  last_success_ms = excluded.last_success_ms,
                  unreachable_since_ms = NULL,
                  producer_version = excluded.producer_version,
                  last_error_code = NULL;
                """;
            state.Parameters.AddWithValue("$source_id", source.SourceId);
            state.Parameters.AddWithValue("$observed_at", observedAtMs);
            state.Parameters.AddWithValue("$producer_version", source.ProducerVersion);
            await state.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task UpsertFindingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ParsedFinding finding,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO findings(
              signature, finding_json, service, finding_type, severity, endpoint,
              template_hash, sample_trace_id, first_seen_ms, last_seen_ms,
              max_confidence, max_confidence_rank)
            VALUES (
              $signature, $finding_json, $service, $finding_type, $severity, $endpoint,
              $template_hash, $sample_trace_id, $observed_at, $observed_at,
              $confidence, $confidence_rank)
            ON CONFLICT(signature) DO UPDATE SET
              finding_json = excluded.finding_json,
              service = excluded.service,
              finding_type = excluded.finding_type,
              severity = excluded.severity,
              endpoint = excluded.endpoint,
              template_hash = excluded.template_hash,
              sample_trace_id = excluded.sample_trace_id,
              first_seen_ms = MIN(findings.first_seen_ms, excluded.first_seen_ms),
              last_seen_ms = MAX(findings.last_seen_ms, excluded.last_seen_ms),
              max_confidence = CASE
                WHEN excluded.max_confidence_rank > findings.max_confidence_rank
                THEN excluded.max_confidence ELSE findings.max_confidence END,
              max_confidence_rank = MAX(findings.max_confidence_rank, excluded.max_confidence_rank);
            """;
        command.Parameters.AddWithValue("$signature", finding.Signature);
        command.Parameters.AddWithValue("$finding_json", finding.EnvelopeJson);
        command.Parameters.AddWithValue("$service", finding.Service);
        command.Parameters.AddWithValue("$finding_type", finding.FindingType);
        command.Parameters.AddWithValue("$severity", finding.Severity);
        command.Parameters.AddWithValue("$endpoint", finding.Endpoint);
        command.Parameters.AddWithValue("$template_hash", finding.TemplateHash);
        command.Parameters.AddWithValue("$sample_trace_id", (object?)finding.TraceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$observed_at", observedAtMs);
        command.Parameters.AddWithValue("$confidence", finding.Confidence);
        command.Parameters.AddWithValue("$confidence_rank", finding.ConfidenceRank);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertFindingSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceSnapshot source,
        ParsedFinding finding,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO finding_sources(
              signature, source_id, source_name, environment, producer_version,
              first_seen_ms, last_seen_ms)
            VALUES (
              $signature, $source_id, $source_name, $environment, $producer_version,
              $observed_at, $observed_at)
            ON CONFLICT(signature, source_id) DO UPDATE SET
              source_name = excluded.source_name,
              environment = excluded.environment,
              producer_version = excluded.producer_version,
              first_seen_ms = MIN(finding_sources.first_seen_ms, excluded.first_seen_ms),
              last_seen_ms = MAX(finding_sources.last_seen_ms, excluded.last_seen_ms);
            """;
        command.Parameters.AddWithValue("$signature", finding.Signature);
        command.Parameters.AddWithValue("$source_id", source.SourceId);
        command.Parameters.AddWithValue("$source_name", source.SourceName);
        command.Parameters.AddWithValue("$environment", source.Environment);
        command.Parameters.AddWithValue("$producer_version", source.ProducerVersion);
        command.Parameters.AddWithValue("$observed_at", observedAtMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertHeartbeatAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceSnapshot source,
        ParsedFinding finding,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO endpoint_heartbeats(source_id, service, endpoint, last_seen_any_ms)
            VALUES ($source_id, $service, $endpoint, $observed_at)
            ON CONFLICT(source_id, service, endpoint) DO UPDATE SET
              last_seen_any_ms = MAX(endpoint_heartbeats.last_seen_any_ms, excluded.last_seen_any_ms);
            """;
        command.Parameters.AddWithValue("$source_id", source.SourceId);
        command.Parameters.AddWithValue("$service", finding.Service);
        command.Parameters.AddWithValue("$endpoint", finding.Endpoint);
        command.Parameters.AddWithValue("$observed_at", observedAtMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public void Dispose() => _initializeGate.Dispose();
}
