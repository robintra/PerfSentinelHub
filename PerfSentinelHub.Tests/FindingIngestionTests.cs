using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
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
