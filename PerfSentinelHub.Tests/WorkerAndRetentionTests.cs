using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

public sealed class WorkerAndRetentionTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-retention-{Guid.NewGuid():N}.db");

    [Theory]
    [InlineData(1, 0.0, 800)]
    [InlineData(1, 1.0, 1200)]
    [InlineData(10, 0.5, 300000)]
    public void Backoff_is_jittered_and_capped(int failures, double sample, int expectedMs) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), Backoff.Delay(failures, sample));

    [Fact]
    public async Task Purge_uses_last_seen_for_findings_and_heartbeats()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = new HubDatabase(
            Options.Create(new HubOptions { DatabasePath = _databasePath }),
            TimeProvider.System);
        await database.InitializeAsync(cancellationToken);
        await using (var connection = await database.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO findings VALUES
                  ('old','{}','svc','type','warning','/old','h',NULL,100,500,'local_batch',1),
                  ('recent','{}','svc','type','warning','/recent','h',NULL,1200,1500,'local_batch',1),
                  ('seen-again','{}','svc','type','warning','/again','h',NULL,100,1500,'local_batch',1);
                INSERT INTO endpoint_heartbeats VALUES
                  ('a','svc','/old',500),
                  ('a','svc','/recent',1500);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await database.PurgeAsync(1000, cancellationToken);

        await using var reopened = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal(2L, await CountAsync(reopened, "findings", cancellationToken));
        Assert.Equal(1L, await CountAsync(reopened, "endpoint_heartbeats", cancellationToken));
    }

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
        File.Delete($"{_databasePath}-shm");
        File.Delete($"{_databasePath}-wal");
    }
}
