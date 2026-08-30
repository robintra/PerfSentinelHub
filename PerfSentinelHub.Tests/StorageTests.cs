using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

public sealed class StorageTests
{
    [Fact]
    public async Task Initialize_is_idempotent_and_creates_the_schema()
    {
        using var fixture = TestDatabase.Create();
        var database = fixture.Database;
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.False(database.IsReady);

        await database.InitializeAsync(cancellationToken);
        await database.InitializeAsync(cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var names = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%';
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            names.Add(reader.GetString(0));
        Assert.Equal(
            ["analysis_runs", "endpoint_heartbeats", "finding_lineage", "finding_sources", "findings",
                "schema_migrations", "source_imports", "source_state"],
            names.Order(StringComparer.Ordinal));
        Assert.True(database.IsReady);
    }

    [Fact]
    public async Task Reinitializing_the_same_file_preserves_rows()
    {
        using var fixture = TestDatabase.Create();
        var path = fixture.DatabasePath;
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = fixture.Database;
        await first.InitializeAsync(cancellationToken);

        await using (var connection = await first.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO source_state(source_id, last_attempt_ms)
                VALUES ('production', 1234);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        using var restarted = TestDatabase.Create(path);
        await restarted.Database.InitializeAsync(cancellationToken);
        await using var reopened = await restarted.Database.OpenConnectionAsync(cancellationToken);
        await using var query = reopened.CreateCommand();
        query.CommandText = "SELECT last_attempt_ms FROM source_state WHERE source_id = 'production';";
        Assert.Equal(1234L, (long)(await query.ExecuteScalarAsync(cancellationToken))!);
    }

    [Fact]
    public async Task Push_into_a_new_source_does_not_record_a_poll_attempt()
    {
        using var fixture = TestDatabase.Create();
        var database = fixture.Database;
        var cancellationToken = TestContext.Current.CancellationToken;
        await database.InitializeAsync(cancellationToken);
        var finding = new ParsedFinding(
            "signature", "{}", "checkout", "slow_sql", "warning", "POST /checkout",
            "template-hash", "trace", "daemon_production", 4, null);

        Assert.True(await database.TryUpsertBatchAsync(
            new SourceSnapshot("push-only", "Push only", "production", "0.11.3"),
            new ParsedBatch([finding], 0),
            1234,
            cancellationToken));

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM source_state WHERE source_id = 'push-only';";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(cancellationToken))!);

        command.CommandText = """
            SELECT producer_version FROM finding_sources
            WHERE source_id = 'push-only' AND signature = 'signature';
            """;
        Assert.Equal("0.11.3", (string)(await command.ExecuteScalarAsync(cancellationToken))!);
    }

    [Fact]
    public async Task Initialize_adds_the_lineage_columns_to_a_pre_migration_database()
    {
        using var fixture = TestDatabase.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Database.InitializeAsync(cancellationToken);

        // Roll one linked row back to the shape finding_lineage had before the
        // origin and depth columns existed. Dropping them beats hand-writing
        // the old DDL: Schema.V1's CREATE TABLE IF NOT EXISTS would leave a
        // duplicated definition behind and it would drift silently.
        await using (var connection = await fixture.Database.OpenConnectionAsync(cancellationToken))
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO findings(
                  signature, finding_json, service, finding_type, severity, endpoint,
                  template_hash, sample_trace_id, first_seen_ms, last_seen_ms,
                  max_confidence, max_confidence_rank)
                VALUES ('successor', '{}', 'order-service', 'n_plus_one_sql', 'warning',
                  'GET /orders', 'hash-b', 'trace', 500, 900, 'daemon_staging', 2);
                INSERT INTO finding_lineage(
                  successor_signature, predecessor_signature, predecessor_first_seen_ms,
                  origin_first_seen_ms, depth, linked_at_ms, method)
                VALUES ('successor', 'predecessor', 100, 100, 1, 500, 'endpoint_template');
                ALTER TABLE finding_lineage DROP COLUMN origin_first_seen_ms;
                ALTER TABLE finding_lineage DROP COLUMN depth;
                """;
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }

        // A NEW instance, because InitializeAsync short-circuits on an
        // in-process flag: the upgrade only ever happens in a fresh process
        // opening a file an older binary wrote.
        using var restarted = TestDatabase.Create(fixture.DatabasePath);
        await restarted.Database.InitializeAsync(cancellationToken);
        // And once more, since a third boot must not replay the ALTER: the
        // probe short-circuits, and a repeated ALTER throws "duplicate column".
        using var restartedAgain = TestDatabase.Create(fixture.DatabasePath);
        await restartedAgain.Database.InitializeAsync(cancellationToken);

        await using var reopened = await restartedAgain.Database.OpenConnectionAsync(cancellationToken);
        await using var read = reopened.CreateCommand();
        read.CommandText = """
            SELECT origin_first_seen_ms, depth FROM finding_lineage
            WHERE successor_signature = 'successor';
            """;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        // Backfilled from the only date the old row carried, not left at the
        // column default: a zero origin would date every pre-migration chain
        // to 1970.
        Assert.Equal(100, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    private sealed class TestDatabase : IDisposable
    {
        private TestDatabase(string path)
        {
            DatabasePath = path;
            Database = new HubDatabase(
                Options.Create(new HubOptions { DatabasePath = path }),
                TimeProvider.System);
        }

        public readonly string DatabasePath;
        public readonly HubDatabase Database;

        public static TestDatabase Create(string? path = null) => new(
            path ?? Path.Combine(
                Path.GetTempPath(),
                $"perf-sentinel-hub-{Guid.NewGuid():N}.db"));

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            File.Delete(DatabasePath);
            File.Delete($"{DatabasePath}-shm");
            File.Delete($"{DatabasePath}-wal");
        }
    }
}
