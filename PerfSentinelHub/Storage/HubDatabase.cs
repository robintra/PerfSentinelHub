using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Api;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Storage;

public sealed class HubDatabase(IOptions<HubOptions> options, TimeProvider timeProvider) : IDisposable
{
    private readonly string _databasePath = options.Value.DatabasePath;
    private readonly int _maxReadLimit = options.Value.MaxReadLimit;
    private readonly long _resolutionGraceMs = (long)options.Value.ResolutionGrace.TotalMilliseconds;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private const int PurgeChunkSize = 5_000;
    private const string SourceIdParameter = "$source_id";
    private const string ObservedAtParameter = "$observed_at";
    private const string FirstSeenParameter = "$first_seen";
    private static readonly TimeSpan WriteGateWait = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
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
                pragmas.CommandText = "PRAGMA journal_mode=WAL;";
                await pragmas.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var transaction = connection.BeginTransaction(deferred: false);
            await using (var migration = connection.CreateCommand())
            {
                migration.Transaction = transaction;
                migration.CommandText = Schema.V1 + Schema.V2;
                await migration.ExecuteNonQueryAsync(cancellationToken);
            }

            await EnsureLineageColumnsAsync(connection, transaction, cancellationToken);

            await using (var version = connection.CreateCommand())
            {
                version.Transaction = transaction;
                version.CommandText = """
                    INSERT OR IGNORE INTO schema_migrations(version, applied_at_ms)
                    VALUES (1, $applied_at_ms), (2, $applied_at_ms);
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

    /// <summary>
    /// CREATE TABLE IF NOT EXISTS never alters a table that already
    /// exists, so a database created before the lineage origin was
    /// denormalized keeps the old three-column shape and every insert
    /// fails on the missing columns. Add them when absent and backfill
    /// from the row itself: every pre-existing link is a single hop, so
    /// its origin is the predecessor's own first_seen and its depth is 1.
    /// </summary>
    private static async Task EnsureLineageColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var probe = connection.CreateCommand())
        {
            probe.Transaction = transaction;
            probe.CommandText = "SELECT name FROM pragma_table_info('finding_lineage');";
            await using var reader = await probe.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                columns.Add(reader.GetString(0));
        }

        if (columns.Count == 0 || columns.Contains("origin_first_seen_ms"))
            return;

        await using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = """
            ALTER TABLE finding_lineage ADD COLUMN origin_first_seen_ms INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE finding_lineage ADD COLUMN depth INTEGER NOT NULL DEFAULT 1;
            UPDATE finding_lineage SET origin_first_seen_ms = predecessor_first_seen_ms;
            """;
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString());

        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // journal_mode is persisted in the file header; the others are per-connection settings.
        command.CommandText =
            "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    public async Task UpsertBatchAsync(
        SourceSnapshot source,
        ParsedBatch batch,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await UpsertBatchCoreAsync(source, batch, observedAtMs, fromPoll: true, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<bool> TryUpsertBatchAsync(
        SourceSnapshot source,
        ParsedBatch batch,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        // Wait out a short write rather than rejecting the push: only genuine contention 503s.
        if (!await _writeGate.WaitAsync(WriteGateWait, cancellationToken))
            return false;
        try
        {
            await UpsertBatchCoreAsync(source, batch, observedAtMs, fromPoll: false, cancellationToken);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task UpsertBatchCoreAsync(
        SourceSnapshot source,
        ParsedBatch batch,
        long observedAtMs,
        bool fromPoll,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);

        foreach (var finding in batch.Findings)
        {
            var firstSeenMs = ClampedFirstSeen(finding, observedAtMs);
            // Resolved before the upsert: a predecessor only exists for a
            // signature the Hub has never stored, and the upsert would hide
            // that distinction.
            var predecessor = await FindLineagePredecessorAsync(
                connection, transaction, finding, observedAtMs, cancellationToken);
            await UpsertFindingAsync(connection, transaction, finding, firstSeenMs, observedAtMs, cancellationToken);
            if (predecessor is not null)
            {
                await InsertLineageAsync(
                    connection, transaction, finding.Signature, predecessor, observedAtMs, cancellationToken);
            }

            await UpsertFindingSourceAsync(
                connection, transaction, source, finding, firstSeenMs, observedAtMs, cancellationToken);
            await UpsertHeartbeatAsync(connection, transaction, source, finding, observedAtMs, cancellationToken);
        }

        await using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            // A push may refresh the version of an existing poll state, but it must not create
            // one: that would report a successful poll before the Hub ever reached the daemon.
            state.CommandText = fromPoll ? """
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
                """ : "UPDATE source_state SET producer_version = $producer_version WHERE source_id = $source_id;";
            state.Parameters.AddWithValue(SourceIdParameter, source.SourceId);
            state.Parameters.AddWithValue(ObservedAtParameter, observedAtMs);
            state.Parameters.AddWithValue("$producer_version", source.ProducerVersion);
            await state.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkSourceAttemptAsync(
        string sourceId,
        long attemptedAtMs,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
            INSERT INTO source_state(source_id, last_attempt_ms)
            VALUES ($source_id, $attempted_at)
            ON CONFLICT(source_id) DO UPDATE SET last_attempt_ms = excluded.last_attempt_ms;
            """;
            command.Parameters.AddWithValue(SourceIdParameter, sourceId);
            command.Parameters.AddWithValue("$attempted_at", attemptedAtMs);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task MarkSourceFailureAsync(
        string sourceId,
        long failedAtMs,
        string errorCode,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
            INSERT INTO source_state(
              source_id, last_attempt_ms, unreachable_since_ms, last_error_code)
            VALUES ($source_id, $failed_at, $failed_at, $error_code)
            ON CONFLICT(source_id) DO UPDATE SET
              last_attempt_ms = excluded.last_attempt_ms,
              unreachable_since_ms = COALESCE(source_state.unreachable_since_ms, excluded.unreachable_since_ms),
              last_error_code = excluded.last_error_code;
            """;
            command.Parameters.AddWithValue(SourceIdParameter, sourceId);
            command.Parameters.AddWithValue("$failed_at", failedAtMs);
            command.Parameters.AddWithValue("$error_code", errorCode);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task PurgeAsync(long cutoffMs, CancellationToken cancellationToken)
    {
        // Chunked: holding the write gate for one whole multi-GB delete would 503 every daemon
        // import for the duration of the purge.
        while (await PurgeChunkAsync(cutoffMs, cancellationToken) > 0)
        {
            // Every chunk is deleted by the call in the condition itself.
        }
    }

    private async Task<int> PurgeChunkAsync(long cutoffMs, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(deferred: false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // findings.last_seen_ms is the MAX across sources, so stale per-source observations and
            // retired sources outlive the window unless they are purged on their own timestamps.
            command.CommandText = """
            DELETE FROM finding_sources WHERE rowid IN (
              SELECT rowid FROM finding_sources WHERE last_seen_ms < $cutoff LIMIT $chunk);
            DELETE FROM findings WHERE rowid IN (
              SELECT rowid FROM findings WHERE last_seen_ms < $cutoff LIMIT $chunk);
            DELETE FROM endpoint_heartbeats WHERE rowid IN (
              SELECT rowid FROM endpoint_heartbeats WHERE last_seen_any_ms < $cutoff LIMIT $chunk);
            DELETE FROM source_state WHERE rowid IN (
              SELECT rowid FROM source_state WHERE last_attempt_ms < $cutoff LIMIT $chunk);
            """;
            command.Parameters.AddWithValue("$cutoff", cutoffMs);
            command.Parameters.AddWithValue("$chunk", PurgeChunkSize);
            var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return deleted;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task<IReadOnlyList<StoredFinding>> QueryFindingsAsync(
        FindingQuery query,
        CancellationToken cancellationToken) =>
        QueryAsync(query, null, cancellationToken);

    public Task<IReadOnlyList<StoredFinding>> FindByTraceAsync(
        string traceId,
        CancellationToken cancellationToken) =>
        QueryAsync(new FindingQuery(null, null, null, _maxReadLimit), traceId, cancellationToken);

    // Derived at read time, never stored: a finding whose endpoint still
    // heartbeats from a reachable source while the finding itself went
    // quiet has presumably been fixed. A quiet endpoint or an unreachable
    // fleet proves nothing, so those stay `not_observed`.
    // The heartbeat must come from a source that observed THIS finding: a
    // sibling source running the same endpoint without ever carrying the
    // finding proves nothing about the source that did. The LEFT JOIN on
    // source_state is load-bearing: a push-only source that never failed
    // has no row there, and an INNER JOIN would wrongly demote it.
    private const string StatusExpression = """
        CASE
          WHEN findings.last_seen_ms >= $status_now - $status_grace THEN 'active'
          WHEN EXISTS (
            SELECT 1 FROM endpoint_heartbeats AS eh
            LEFT JOIN source_state AS hs ON hs.source_id = eh.source_id
            WHERE eh.service = findings.service
              AND eh.endpoint = findings.endpoint
              AND eh.last_seen_any_ms >= findings.last_seen_ms + $status_grace
              AND hs.unreachable_since_ms IS NULL
              AND EXISTS (
                SELECT 1 FROM finding_sources AS fs
                WHERE fs.signature = findings.signature
                  AND fs.source_id = eh.source_id
              )
          ) THEN 'likely_resolved'
          ELSE 'not_observed'
        END
        """;

    private async Task<IReadOnlyList<StoredFinding>> QueryAsync(
        FindingQuery query,
        string? traceId,
        CancellationToken cancellationToken)
    {
        var where = new StringBuilder("WHERE 1 = 1");
        var parameters = new List<(string Name, object Value)>();
        AddFilter(where, parameters, "service", "$service", query.Service);
        AddFilter(where, parameters, "finding_type", "$finding_type", query.FindingType);
        AddFilter(where, parameters, "severity", "$severity", query.Severity);
        AddFilter(where, parameters, "sample_trace_id", "$trace_id", traceId);
        if (!query.IncludeAcked)
            where.Append(" AND json_extract(finding_json, '$.acknowledged_by') IS NULL");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // The status is computed before LIMIT so a status filter fills its
        // page instead of returning whatever survived a post-filter.
        command.CommandText = $"""
            WITH statused AS (
              SELECT findings.*, {StatusExpression} AS status
              FROM findings
              {where}
            ),
            selected AS (
              SELECT * FROM statused
              WHERE $status IS NULL OR status = $status
              ORDER BY last_seen_ms DESC, signature ASC
              LIMIT $limit
            )
            SELECT
              f.signature, f.finding_json, f.first_seen_ms, f.last_seen_ms, f.max_confidence,
              fs.source_id, fs.source_name, fs.environment, fs.producer_version,
              fs.last_seen_ms, ss.unreachable_since_ms, f.status,
              fl.origin_first_seen_ms, fl.depth
            FROM selected AS f
            LEFT JOIN finding_sources AS fs ON fs.signature = f.signature
            LEFT JOIN source_state AS ss ON ss.source_id = fs.source_id
            LEFT JOIN finding_lineage AS fl ON fl.successor_signature = f.signature
            ORDER BY f.last_seen_ms DESC, f.signature ASC, fs.source_id ASC;
            """;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.Parameters.AddWithValue("$limit", query.Limit);
        command.Parameters.AddWithValue("$status", (object?)query.Status ?? DBNull.Value);
        command.Parameters.AddWithValue("$status_now", timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$status_grace", _resolutionGraceMs);

        var rows = new List<StoredFinding>();
        var bySignature = new Dictionary<string, StoredFinding>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var signature = reader.GetString(0);
                if (!bySignature.TryGetValue(signature, out var finding))
                {
                    var sources = new List<FindingSourceObservation>();
                    finding = new StoredFinding(
                        signature,
                        reader.GetString(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetString(4),
                        reader.GetString(11),
                        sources);
                    if (!reader.IsDBNull(12))
                        finding.Lineage = new LineageInfo(reader.GetInt64(12), reader.GetInt32(13));
                    bySignature.Add(signature, finding);
                    rows.Add(finding);
                }

                if (!reader.IsDBNull(5))
                {
                    finding.Sources.Add(new FindingSourceObservation(
                        reader.GetString(5),
                        reader.GetString(6),
                        reader.GetString(7),
                        reader.GetString(8),
                        reader.GetInt64(9),
                        reader.IsDBNull(10) ? null : reader.GetInt64(10)));
                }
            }
        }

        return rows;
    }

    private static void AddFilter(
        StringBuilder where,
        List<(string Name, object Value)> parameters,
        string column,
        string parameter,
        string? value)
    {
        if (value is null)
            return;

        where.Append(" AND ").Append(column).Append(" = ").Append(parameter);
        parameters.Add((parameter, value));
    }

    // The daemon-reported birth wins over the Hub clock, clamped so a clock
    // running ahead cannot mint future observations. last_seen stays the Hub
    // clock on purpose: retention, ORDER BY and the freshness guard compare
    // it, so it must come from one monotonic clock, never a remote one.
    private static long ClampedFirstSeen(ParsedFinding finding, long observedAtMs) =>
        Math.Min(finding.FirstSeenMs ?? observedAtMs, observedAtMs);

    // A predecessor must still be a live problem when its successor appears:
    // an endpoint whose finding died out months ago is a new story, not a
    // template mutation.
    private static readonly long LineageWindowMs = (long)TimeSpan.FromDays(30).TotalMilliseconds;

    /// <summary>
    /// The lone stored finding the incoming one most likely mutated from:
    /// same service, detector and endpoint, different template hash, seen
    /// within the lineage window. Null for an already-known signature (the
    /// steady-state path, answered by a cheap primary-key probe before any
    /// candidate scan), and null with several candidates, where naming one
    /// would be a guess.
    /// </summary>
    private static async Task<LineageCandidate?> FindLineagePredecessorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ParsedFinding finding,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        await using (var known = connection.CreateCommand())
        {
            known.Transaction = transaction;
            known.CommandText = "SELECT 1 FROM findings WHERE signature = $signature;";
            known.Parameters.AddWithValue("$signature", finding.Signature);
            if (await known.ExecuteScalarAsync(cancellationToken) is not null)
                return null;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT candidate.signature, candidate.first_seen_ms,
                   lineage.origin_first_seen_ms, lineage.depth
            FROM findings AS candidate
            LEFT JOIN finding_lineage AS lineage
              ON lineage.successor_signature = candidate.signature
            WHERE candidate.service = $service
              AND candidate.finding_type = $finding_type
              AND candidate.endpoint = $endpoint
              AND candidate.template_hash <> $template_hash
              AND candidate.last_seen_ms >= $window_start
              -- Strictly earlier: two findings of the same batch are two
              -- current problems, not a mutation.
              AND candidate.last_seen_ms < $observed_at
              -- Already superseded: without this, the second mutation in a
              -- chain always sees two candidates and never links.
              AND candidate.signature NOT IN (SELECT predecessor_signature FROM finding_lineage)
            LIMIT 2;
            """;
        command.Parameters.AddWithValue("$service", finding.Service);
        command.Parameters.AddWithValue("$finding_type", finding.FindingType);
        command.Parameters.AddWithValue("$endpoint", finding.Endpoint);
        command.Parameters.AddWithValue("$template_hash", finding.TemplateHash);
        command.Parameters.AddWithValue("$window_start", observedAtMs - LineageWindowMs);
        command.Parameters.AddWithValue(ObservedAtParameter, observedAtMs);

        LineageCandidate? candidate = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (candidate is not null)
                return null;
            var firstSeen = reader.GetInt64(1);
            // Inherit the predecessor's own chain when it has one, so the
            // origin and depth live on this row and survive every purge.
            var origin = reader.IsDBNull(2) ? firstSeen : Math.Min(reader.GetInt64(2), firstSeen);
            var depth = reader.IsDBNull(3) ? 1 : reader.GetInt32(3) + 1;
            candidate = new LineageCandidate(reader.GetString(0), firstSeen, origin, depth);
        }

        return candidate;
    }

    private static async Task InsertLineageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string successorSignature,
        LineageCandidate predecessor,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // OR IGNORE: a replayed import must not fail on the existing link.
        command.CommandText = """
            INSERT OR IGNORE INTO finding_lineage(
              successor_signature, predecessor_signature, predecessor_first_seen_ms,
              origin_first_seen_ms, depth, linked_at_ms, method)
            VALUES ($successor, $predecessor, $predecessor_first_seen,
              $origin_first_seen, $depth, $linked_at, 'endpoint_template');
            """;
        command.Parameters.AddWithValue("$successor", successorSignature);
        command.Parameters.AddWithValue("$predecessor", predecessor.Signature);
        command.Parameters.AddWithValue("$predecessor_first_seen", predecessor.FirstSeenMs);
        command.Parameters.AddWithValue("$origin_first_seen", predecessor.OriginFirstSeenMs);
        command.Parameters.AddWithValue("$depth", predecessor.Depth);
        command.Parameters.AddWithValue("$linked_at", observedAtMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record LineageCandidate(string Signature, long FirstSeenMs, long OriginFirstSeenMs, int Depth);

    private static async Task UpsertFindingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ParsedFinding finding,
        long firstSeenMs,
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
              $template_hash, $sample_trace_id, $first_seen, $observed_at,
              $confidence, $confidence_rank)
            ON CONFLICT(signature) DO UPDATE SET
              -- Only a newer observation may replace the envelope and its indexed columns:
              -- a slower source polled earlier must not overwrite a fresher severity or trace.
              finding_json = IIF(excluded.last_seen_ms >= findings.last_seen_ms,
                excluded.finding_json, findings.finding_json),
              service = IIF(excluded.last_seen_ms >= findings.last_seen_ms,
                excluded.service, findings.service),
              finding_type = IIF(excluded.last_seen_ms >= findings.last_seen_ms,
                excluded.finding_type, findings.finding_type),
              severity = IIF(excluded.last_seen_ms >= findings.last_seen_ms,
                excluded.severity, findings.severity),
              endpoint = IIF(excluded.last_seen_ms >= findings.last_seen_ms,
                excluded.endpoint, findings.endpoint),
              template_hash = IIF(excluded.last_seen_ms >= findings.last_seen_ms,
                excluded.template_hash, findings.template_hash),
              sample_trace_id = IIF(excluded.last_seen_ms >= findings.last_seen_ms,
                excluded.sample_trace_id, findings.sample_trace_id),
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
        command.Parameters.AddWithValue(FirstSeenParameter, firstSeenMs);
        command.Parameters.AddWithValue(ObservedAtParameter, observedAtMs);
        command.Parameters.AddWithValue("$confidence", finding.Confidence);
        command.Parameters.AddWithValue("$confidence_rank", finding.ConfidenceRank);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertFindingSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceSnapshot source,
        ParsedFinding finding,
        long firstSeenMs,
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
              $first_seen, $observed_at)
            ON CONFLICT(signature, source_id) DO UPDATE SET
              source_name = excluded.source_name,
              environment = excluded.environment,
              producer_version = excluded.producer_version,
              first_seen_ms = MIN(finding_sources.first_seen_ms, excluded.first_seen_ms),
              last_seen_ms = MAX(finding_sources.last_seen_ms, excluded.last_seen_ms);
            """;
        command.Parameters.AddWithValue("$signature", finding.Signature);
        command.Parameters.AddWithValue(SourceIdParameter, source.SourceId);
        command.Parameters.AddWithValue("$source_name", source.SourceName);
        command.Parameters.AddWithValue("$environment", source.Environment);
        command.Parameters.AddWithValue("$producer_version", source.ProducerVersion);
        command.Parameters.AddWithValue(FirstSeenParameter, firstSeenMs);
        command.Parameters.AddWithValue(ObservedAtParameter, observedAtMs);
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
        command.Parameters.AddWithValue(SourceIdParameter, source.SourceId);
        command.Parameters.AddWithValue("$service", finding.Service);
        command.Parameters.AddWithValue("$endpoint", finding.Endpoint);
        command.Parameters.AddWithValue(ObservedAtParameter, observedAtMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public void Dispose()
    {
        _initializeGate.Dispose();
        _writeGate.Dispose();
    }
}
