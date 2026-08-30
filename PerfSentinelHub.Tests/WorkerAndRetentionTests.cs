using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Maintenance;
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
    public async Task Purge_uses_last_seen_for_findings_sources_heartbeats_and_state()
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
                INSERT INTO finding_sources VALUES
                  ('seen-again','stale','Stale','staging','0.11.2',100,500),
                  ('seen-again','fresh','Fresh','production','0.11.2',100,1500);
                INSERT INTO source_state(source_id, last_attempt_ms) VALUES
                  ('retired',500),
                  ('active',1500);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await database.PurgeAsync(1000, 0, cancellationToken);

        await using var reopened = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal(2L, await CountAsync(reopened, "findings", cancellationToken));
        Assert.Equal(1L, await CountAsync(reopened, "endpoint_heartbeats", cancellationToken));
        Assert.Equal(1L, await CountAsync(reopened, "finding_sources", cancellationToken));
        Assert.Equal(1L, await CountAsync(reopened, "source_state", cancellationToken));
    }

    [Fact]
    public async Task Poll_worker_keeps_running_when_a_storage_write_fails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = Options.Create(new HubOptions
        {
            DatabasePath = UnwritablePath,
            Sources = [new SourceOptions
            {
                Id = "unreachable",
                Name = "Unreachable",
                Environment = "test",
                BaseUrl = new Uri("http://127.0.0.1:1")
            }]
        });
        var clock = new FakeTimeProvider();
        var logger = new ListLogger<PollWorker>();
        var poller = new SourcePoller(
            new DaemonClient(new HttpClient(), options),
            new HubDatabase(options, clock),
            clock,
            NullLogger<SourcePoller>.Instance);
        using var worker = new PollWorker(poller, options, clock, logger);

        await worker.StartAsync(cancellationToken);
        await WaitForAsync(() => logger.Messages.Count > 0);

        Assert.Contains(logger.Messages, message => message.Contains("unreachable", StringComparison.Ordinal));
        Assert.False(worker.ExecuteTask!.IsCompleted);
        await StopQuietlyAsync(worker, cancellationToken);
    }

    [Fact]
    public async Task A_trace_backend_is_never_polled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = Options.Create(new HubOptions
        {
            DatabasePath = UnwritablePath,
            Sources = [new SourceOptions
            {
                Id = "victoria",
                Name = "Victoria Traces",
                Environment = "test",
                Kind = SourceKinds.JaegerQuery,
                BaseUrl = new Uri("http://127.0.0.1:1")
            }]
        });
        var clock = new FakeTimeProvider();
        var poller = new SourcePoller(
            new DaemonClient(new HttpClient(), options),
            new HubDatabase(options, clock),
            clock,
            NullLogger<SourcePoller>.Instance);
        using var worker = new PollWorker(poller, options, clock, NullLogger<PollWorker>.Instance);

        await worker.StartAsync(cancellationToken);
        await WaitForAsync(() => worker.ExecuteTask!.IsCompleted);

        // The daemon case above never completes because it polls forever. With
        // nothing pollable the loop has no source at all, which is the point:
        // a backend serves no findings endpoint and would be marked
        // unreachable on every interval.
        Assert.True(worker.ExecuteTask!.IsCompleted);
        await StopQuietlyAsync(worker, cancellationToken);
    }

    [Fact]
    public async Task Retention_worker_keeps_running_when_a_purge_fails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = Options.Create(new HubOptions { DatabasePath = UnwritablePath });
        var clock = new FakeTimeProvider();
        var logger = new ListLogger<RetentionWorker>();
        using var worker = new RetentionWorker(new HubDatabase(options, clock), options, clock, logger);

        await worker.StartAsync(cancellationToken);
        await WaitForAsync(() => logger.Messages.Count > 0);

        Assert.False(worker.ExecuteTask!.IsCompleted);
        await StopQuietlyAsync(worker, cancellationToken);
    }

    private static string UnwritablePath => Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-missing-{Guid.NewGuid():N}",
        "hub.db");

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
            await Task.Delay(10);
    }

    private static async Task StopQuietlyAsync(
        BackgroundService worker,
        CancellationToken cancellationToken)
    {
        try
        {
            await worker.StopAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The stop token cancels the worker loop; that is the expected shutdown path.
        }
    }

    [Fact]
    public async Task Purge_removes_finished_runs_and_never_an_unfinished_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = new HubDatabase(
            Options.Create(new HubOptions { DatabasePath = _databasePath }),
            TimeProvider.System);
        await database.InitializeAsync(cancellationToken);
        await using (var connection = await database.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            // finished_at_ms is the 11th column; a queued run has none, which is
            // why the purge falls back to created_at_ms rather than skipping it.
            command.CommandText = """
                INSERT INTO analysis_runs
                  (id,status,source_id,source_name,environment,kind,request_json,
                   requested_by,created_at_ms,started_at_ms,finished_at_ms)
                VALUES
                  ('old-done','succeeded','a','A','prod','daemon','{}','u',100,100,500),
                  ('fresh-done','succeeded','a','A','prod','daemon','{}','u',1200,1200,1500),
                  ('old-expired','expired','a','A','prod','daemon','{}','u',100,100,500),
                  ('ancient-pending','pending','a','A','prod','daemon','{}','u',1,NULL,NULL),
                  ('ancient-running','running','a','A','prod','daemon','{}','u',1,1,NULL);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await database.PurgeAsync(0, 1000, cancellationToken);

        await using var reopened = await database.OpenConnectionAsync(cancellationToken);
        var surviving = await IdsAsync(reopened, cancellationToken);
        // A run still queued or in flight is never purged, however old its row
        // looks: a worker is about to write to it, or already is.
        Assert.Equal(
            new[] { "ancient-pending", "ancient-running", "fresh-done" },
            surviving);
    }

    private static async Task<string[]> IdsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM analysis_runs ORDER BY id;";
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            ids.Add(reader.GetString(0));
        return [.. ids];
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
