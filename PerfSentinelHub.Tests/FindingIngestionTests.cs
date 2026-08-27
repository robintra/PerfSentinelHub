using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

public sealed class FindingIngestionTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-ingestion-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Parser_preserves_the_opaque_envelope_and_indexes_required_fields()
    {
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(
            FixturePath,
            TestContext.Current.CancellationToken));

        var finding = Assert.Single(batch.Findings);
        Assert.Equal("blocking_wait:rider-smoke:checkout:slow-path", finding.Signature);
        Assert.Equal("rider-smoke", finding.Service);
        Assert.Equal("POST /checkout", finding.Endpoint);
        Assert.Equal(1786183200000L, finding.FirstSeenMs);
        Assert.Contains("future_contract_field", finding.EnvelopeJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upsert_merges_signature_and_tracks_each_source_and_heartbeat()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));

        await database.UpsertBatchAsync(
            new SourceSnapshot("production-a", "Production A", "production", "0.11.2"),
            batch,
            1000,
            cancellationToken);
        await database.UpsertBatchAsync(
            new SourceSnapshot("staging-a", "Staging A", "staging", "0.11.2"),
            batch,
            2000,
            cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM findings;", cancellationToken));
        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM finding_sources;", cancellationToken));
        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM endpoint_heartbeats;", cancellationToken));
        Assert.Equal(1000L, await ScalarAsync(connection, "SELECT first_seen_ms FROM findings;", cancellationToken));
        Assert.Equal(2000L, await ScalarAsync(connection, "SELECT last_seen_ms FROM findings;", cancellationToken));
        Assert.Equal(
            "daemon_production",
            await TextScalarAsync(connection, "SELECT max_confidence FROM findings;", cancellationToken));
    }

    [Fact]
    public async Task Upsert_keeps_the_newest_observation_when_an_older_one_commits_late()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var older = batch.Findings[0] with { Severity = "warning", TraceId = "stale-trace" };

        await database.UpsertBatchAsync(
            new SourceSnapshot("production-a", "Production A", "production", "0.11.2"),
            batch,
            2000,
            cancellationToken);
        await database.UpsertBatchAsync(
            new SourceSnapshot("staging-a", "Staging A", "staging", "0.11.2"),
            new ParsedBatch([older], 0),
            1000,
            cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal(
            "critical",
            await TextScalarAsync(connection, "SELECT severity FROM findings;", cancellationToken));
        Assert.Equal(
            "rider-trace-file-line",
            await TextScalarAsync(connection, "SELECT sample_trace_id FROM findings;", cancellationToken));
        Assert.Equal(
            2000L,
            await ScalarAsync(connection, "SELECT last_seen_ms FROM findings;", cancellationToken));
    }

    [Fact]
    public async Task Upsert_honors_the_daemon_reported_first_seen_and_keeps_the_hub_clock_for_last_seen()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        // A realistic poll clock: after the fixture's first_seen_ms, so the
        // daemon-reported birth survives the clamp instead of being cut to it.
        const long firstObservedAt = 1786190000000;
        const long secondObservedAt = firstObservedAt + 3_600_000;
        var source = new SourceSnapshot("production-a", "Production A", "production", "0.11.2");

        await database.UpsertBatchAsync(source, batch, firstObservedAt, cancellationToken);
        await database.UpsertBatchAsync(source, batch, secondObservedAt, cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal(
            1786183200000L,
            await ScalarAsync(connection, "SELECT first_seen_ms FROM findings;", cancellationToken));
        Assert.Equal(
            secondObservedAt,
            await ScalarAsync(connection, "SELECT last_seen_ms FROM findings;", cancellationToken));
        Assert.Equal(
            1786183200000L,
            await ScalarAsync(connection, "SELECT first_seen_ms FROM finding_sources;", cancellationToken));
        Assert.Equal(
            secondObservedAt,
            await ScalarAsync(connection, "SELECT last_seen_ms FROM finding_sources;", cancellationToken));
    }

    [Fact]
    public async Task Parser_rejects_a_first_seen_below_the_epoch_ms_sanity_floor()
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllBytesAsync(
            FixturePath,
            TestContext.Current.CancellationToken));
        // A seconds-unit bug: the same instant, a thousand times smaller.
        var seconds = fixture.RootElement[0].GetRawText()
            .Replace("1786183200000", "1786183200", StringComparison.Ordinal);
        var payload = System.Text.Encoding.UTF8.GetBytes($"[{seconds}]");

        var batch = FindingParser.Parse(payload);

        var finding = Assert.Single(batch.Findings);
        Assert.Null(finding.FirstSeenMs);
    }

    [Fact]
    public async Task Parser_rejects_only_the_invalid_array_element()
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllBytesAsync(
            FixturePath,
            TestContext.Current.CancellationToken));
        var valid = fixture.RootElement[0].GetRawText();
        var payload = System.Text.Encoding.UTF8.GetBytes($"[{valid},{{\"finding\":{{}}}},{valid}]");

        var batch = FindingParser.Parse(payload);

        Assert.Equal(2, batch.Findings.Count);
        Assert.Equal(1, batch.RejectedCount);
    }

    [Fact]
    public async Task Upsert_rolls_back_the_whole_batch_when_a_related_write_fails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        await using (var connection = await database.OpenConnectionAsync(cancellationToken))
        await using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER fail_finding_source BEFORE INSERT ON finding_sources
                BEGIN SELECT RAISE(ABORT, 'test rollback'); END;
                """;
            await trigger.ExecuteNonQueryAsync(cancellationToken);
        }
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));

        await Assert.ThrowsAsync<SqliteException>(() => database.UpsertBatchAsync(
            new SourceSnapshot("production-a", "Production A", "production", "0.11.2"),
            batch,
            1000,
            cancellationToken));

        await using var reopened = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal(0L, await ScalarAsync(reopened, "SELECT COUNT(*) FROM findings;", cancellationToken));
    }

    [Fact]
    public async Task Upsert_links_a_template_mutation_to_its_lone_predecessor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var source = new SourceSnapshot("production-a", "Production A", "production", "0.11.2");
        await database.UpsertBatchAsync(source, batch, 1000, cancellationToken);

        var mutated = batch.Findings[0] with
        {
            Signature = "blocking_wait:rider-smoke:checkout:mutated-path",
            TemplateHash = "mutated-template-hash",
        };
        await database.UpsertBatchAsync(source, new ParsedBatch([mutated], 0), 2000, cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM finding_lineage;", cancellationToken));
        Assert.Equal(
            batch.Findings[0].Signature,
            await TextScalarAsync(connection, "SELECT predecessor_signature FROM finding_lineage;", cancellationToken));
        Assert.Equal(
            1000L,
            await ScalarAsync(connection, "SELECT predecessor_first_seen_ms FROM finding_lineage;", cancellationToken));
    }

    [Fact]
    public async Task Upsert_does_not_guess_between_two_lineage_candidates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var source = new SourceSnapshot("production-a", "Production A", "production", "0.11.2");
        var second = batch.Findings[0] with
        {
            Signature = "blocking_wait:rider-smoke:checkout:other-path",
            TemplateHash = "other-template-hash",
        };
        // Same batch: two current problems on the endpoint, never a mutation.
        await database.UpsertBatchAsync(
            source, new ParsedBatch([batch.Findings[0], second], 0), 1000, cancellationToken);

        var mutated = batch.Findings[0] with
        {
            Signature = "blocking_wait:rider-smoke:checkout:mutated-path",
            TemplateHash = "mutated-template-hash",
        };
        await database.UpsertBatchAsync(source, new ParsedBatch([mutated], 0), 2000, cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM finding_lineage;", cancellationToken));
    }

    [Fact]
    public async Task Query_walks_the_lineage_chain_to_the_original_first_seen()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var source = new SourceSnapshot("production-a", "Production A", "production", "0.11.2");
        await database.UpsertBatchAsync(source, batch, 1000, cancellationToken);
        var second = batch.Findings[0] with
        {
            Signature = "blocking_wait:rider-smoke:checkout:v2",
            TemplateHash = "hash-v2",
        };
        await database.UpsertBatchAsync(source, new ParsedBatch([second], 0), 2000, cancellationToken);
        var third = second with
        {
            Signature = "blocking_wait:rider-smoke:checkout:v3",
            TemplateHash = "hash-v3",
        };
        await database.UpsertBatchAsync(source, new ParsedBatch([third], 0), 3000, cancellationToken);

        var rows = await database.QueryFindingsAsync(
            new PerfSentinelHub.Api.FindingQuery(null, null, null, 100), cancellationToken);

        // v2 replaced v1, so only v3 keeps a live predecessor chain of 2.
        var successor = Assert.Single(rows, row => row.Signature == "blocking_wait:rider-smoke:checkout:v3");
        Assert.NotNull(successor.Lineage);
        Assert.Equal(2, successor.Lineage!.Predecessors);
        Assert.Equal(1000L, successor.Lineage.OriginalFirstSeenMs);
    }

    /// <summary>
    /// CREATE TABLE IF NOT EXISTS is a no-op on an existing table, so a
    /// database written before the denormalization must be upgraded in
    /// place rather than left with the old three-column lineage table.
    /// </summary>
    [Fact]
    public async Task Initialize_adds_the_lineage_columns_to_a_pre_denormalization_database()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var seed = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await seed.OpenAsync(cancellationToken);
            await using var create = seed.CreateCommand();
            create.CommandText = """
                CREATE TABLE finding_lineage (
                  successor_signature TEXT NOT NULL,
                  predecessor_signature TEXT NOT NULL,
                  predecessor_first_seen_ms INTEGER NOT NULL,
                  linked_at_ms INTEGER NOT NULL,
                  method TEXT NOT NULL,
                  PRIMARY KEY(successor_signature, predecessor_signature)
                );
                INSERT INTO finding_lineage VALUES ('v2', 'v1', 1000, 2000, 'endpoint_template');
                """;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var check = connection.CreateCommand();
        check.CommandText =
            "SELECT origin_first_seen_ms, depth FROM finding_lineage WHERE successor_signature = 'v2';";
        await using var reader = await check.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal(1000L, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt32(1));
        await reader.DisposeAsync();

        // The columns existing is not enough: the upgraded table must
        // accept the insert the production path issues.
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO finding_lineage(
              successor_signature, predecessor_signature, predecessor_first_seen_ms,
              origin_first_seen_ms, depth, linked_at_ms, method)
            VALUES ('v3', 'v2', 2000, 1000, 2, 3000, 'endpoint_template');
            """;
        Assert.Equal(1, await insert.ExecuteNonQueryAsync(cancellationToken));
    }

    /// <summary>
    /// The chain's origin is denormalized at link time, so purging the
    /// intermediate hop must not shorten the surviving finding's lineage.
    /// </summary>
    [Fact]
    public async Task Lineage_survives_the_purge_of_an_intermediate_hop()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var source = new SourceSnapshot("production-a", "Production A", "production", "0.11.2");
        await database.UpsertBatchAsync(source, batch, 1000, cancellationToken);
        var second = batch.Findings[0] with
        {
            Signature = "blocking_wait:rider-smoke:checkout:v2",
            TemplateHash = "hash-v2",
        };
        await database.UpsertBatchAsync(source, new ParsedBatch([second], 0), 2000, cancellationToken);
        var third = second with
        {
            Signature = "blocking_wait:rider-smoke:checkout:v3",
            TemplateHash = "hash-v3",
        };
        await database.UpsertBatchAsync(source, new ParsedBatch([third], 0), 3000, cancellationToken);

        // Purge v1 and v2 (last_seen 1000 and 2000), keep v3.
        await database.PurgeAsync(2500, cancellationToken);

        var rows = await database.QueryFindingsAsync(
            new PerfSentinelHub.Api.FindingQuery(null, null, null, 100), cancellationToken);
        var survivor = Assert.Single(rows, row => row.Signature == "blocking_wait:rider-smoke:checkout:v3");
        Assert.NotNull(survivor.Lineage);
        Assert.Equal(2, survivor.Lineage!.Predecessors);
        Assert.Equal(1000L, survivor.Lineage.OriginalFirstSeenMs);
    }

    /// <summary>
    /// A heartbeat from a source that never carried the finding proves
    /// nothing about the source that did: the status must stay
    /// not_observed while the witnessing source is silent.
    /// </summary>
    [Fact]
    public async Task Status_ignores_heartbeats_from_sources_that_never_saw_the_finding()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_786_190_000_000));
        var database = new HubDatabase(
            Options.Create(new HubOptions { DatabasePath = _databasePath }),
            clock);
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var witnessing = new SourceSnapshot("production-a", "Production A", "production", "0.11.2");
        await database.UpsertBatchAsync(
            witnessing, batch, clock.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken);

        clock.Advance(TimeSpan.FromDays(8));
        // A sibling source heartbeats the same service and endpoint through
        // a finding of its own, while production-a stays silent.
        var sibling = new SourceSnapshot("production-b", "Production B", "production", "0.11.2");
        var other = batch.Findings[0] with
        {
            Signature = "blocking_wait:rider-smoke:checkout:other",
            TemplateHash = "other-hash",
        };
        await database.UpsertBatchAsync(
            sibling,
            new ParsedBatch([other], 0),
            clock.GetUtcNow().ToUnixTimeMilliseconds(),
            cancellationToken);

        var rows = await database.QueryFindingsAsync(
            new PerfSentinelHub.Api.FindingQuery(null, null, null, 100), cancellationToken);
        Assert.Equal(
            "not_observed",
            Assert.Single(rows, row => row.Signature == batch.Findings[0].Signature).Status);
    }

    [Fact]
    public async Task Status_derives_from_heartbeats_and_source_reachability()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_786_190_000_000));
        var database = new HubDatabase(
            Options.Create(new HubOptions { DatabasePath = _databasePath }),
            clock);
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var source = new SourceSnapshot("production-a", "Production A", "production", "0.11.2");
        var query = new PerfSentinelHub.Api.FindingQuery(null, null, null, 100);

        var seededAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
        await database.UpsertBatchAsync(source, batch, seededAt, cancellationToken);
        var seeded = Assert.Single(await database.QueryFindingsAsync(query, cancellationToken));
        Assert.Equal("active", seeded.Status);

        // Past the grace with a silent endpoint: nothing proves anything.
        clock.Advance(TimeSpan.FromDays(8));
        var quiet = Assert.Single(await database.QueryFindingsAsync(query, cancellationToken));
        Assert.Equal("not_observed", quiet.Status);

        // The endpoint heartbeats again through another finding while the
        // old one stays silent: presumably fixed.
        var other = batch.Findings[0] with
        {
            Signature = "blocking_wait:rider-smoke:checkout:other",
            TemplateHash = "other-hash",
        };
        await database.UpsertBatchAsync(
            source,
            new ParsedBatch([other], 0),
            clock.GetUtcNow().ToUnixTimeMilliseconds(),
            cancellationToken);
        var rows = await database.QueryFindingsAsync(query, cancellationToken);
        Assert.Equal(
            "likely_resolved",
            Assert.Single(rows, row => row.Signature == batch.Findings[0].Signature).Status);

        // An unreachable source withdraws the presumption.
        await database.MarkSourceFailureAsync(
            "production-a",
            clock.GetUtcNow().ToUnixTimeMilliseconds(),
            "timeout",
            cancellationToken);
        rows = await database.QueryFindingsAsync(query, cancellationToken);
        Assert.Equal(
            "not_observed",
            Assert.Single(rows, row => row.Signature == batch.Findings[0].Signature).Status);
    }

    [Fact]
    public async Task Status_filter_applies_before_the_page_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_786_190_000_000));
        var database = new HubDatabase(
            Options.Create(new HubOptions { DatabasePath = _databasePath }),
            clock);
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var source = new SourceSnapshot("production-a", "Production A", "production", "0.11.2");
        await database.UpsertBatchAsync(
            source, batch, clock.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken);
        clock.Advance(TimeSpan.FromDays(8));
        var fresh = batch.Findings[0] with
        {
            Signature = "blocking_wait:rider-smoke:checkout:fresh",
            TemplateHash = "fresh-hash",
        };
        await database.UpsertBatchAsync(
            source,
            new ParsedBatch([fresh], 0),
            clock.GetUtcNow().ToUnixTimeMilliseconds(),
            cancellationToken);

        var active = await database.QueryFindingsAsync(
            new PerfSentinelHub.Api.FindingQuery(null, null, null, 1, Status: "active"), cancellationToken);
        Assert.Equal("blocking_wait:rider-smoke:checkout:fresh", Assert.Single(active).Signature);

        var resolved = await database.QueryFindingsAsync(
            new PerfSentinelHub.Api.FindingQuery(null, null, null, 1, Status: "likely_resolved"),
            cancellationToken);
        Assert.Equal(batch.Findings[0].Signature, Assert.Single(resolved).Signature);
    }

    private HubDatabase CreateDatabase() => new(
        Options.Create(new HubOptions { DatabasePath = _databasePath }),
        TimeProvider.System);

    private static async Task<long> ScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<string> TextScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
        File.Delete($"{_databasePath}-shm");
        File.Delete($"{_databasePath}-wal");
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "daemon-findings-0.11.2.json");
}
