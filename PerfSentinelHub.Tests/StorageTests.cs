using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
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
        var names = await ReadTableNames(connection, cancellationToken);
        Assert.Equal(
            ["endpoint_heartbeats", "finding_sources", "findings", "schema_migrations", "source_state"],
            names.Order(StringComparer.Ordinal));
        Assert.True(database.IsReady);
    }

    [Fact]
    public async Task Reinitializing_the_same_file_preserves_rows()
    {
        using var fixture = TestDatabase.Create();
        var path = fixture.Path;
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

    private static async Task<IReadOnlyList<string>> ReadTableNames(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var names = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%';
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            names.Add(reader.GetString(0));
        return names;
    }

    private sealed class TestDatabase : IDisposable
    {
        private TestDatabase(string path)
        {
            Path = path;
            Database = new HubDatabase(
                Options.Create(new HubOptions { DatabasePath = path }),
                TimeProvider.System);
        }

        public string Path { get; }
        public HubDatabase Database { get; }

        public static TestDatabase Create(string? path = null) => new(
            path ?? System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"perf-sentinel-hub-{Guid.NewGuid():N}.db"));

        public void Dispose()
        {
            Database.Dispose();
            SqliteConnection.ClearAllPools();
            File.Delete(Path);
            File.Delete($"{Path}-shm");
            File.Delete($"{Path}-wal");
        }
    }
}
