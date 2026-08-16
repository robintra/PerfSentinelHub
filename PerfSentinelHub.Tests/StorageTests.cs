using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

public sealed class StorageTests
{
    [Fact]
    public async Task Initialize_is_idempotent_and_creates_the_v1_schema()
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
            ["endpoint_heartbeats", "finding_sources", "findings", "schema_migrations", "source_state"],
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
            "template-hash", "trace", "daemon_production", 4, null, null);

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
            Database.Dispose();
            SqliteConnection.ClearAllPools();
            File.Delete(DatabasePath);
            File.Delete($"{DatabasePath}-shm");
            File.Delete($"{DatabasePath}-wal");
        }
    }
}
