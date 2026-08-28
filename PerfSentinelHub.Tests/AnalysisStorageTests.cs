using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

public sealed class AnalysisStorageTests : IDisposable
{
    private const long Now = 1_787_839_140_000;

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-runs-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task A_claimed_run_is_taken_once_and_the_oldest_goes_first()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await OpenAsync(cancellationToken);
        await database.TryInsertRunAsync(Run("second", Now + 1000), cancellationToken);
        await database.TryInsertRunAsync(Run("first", Now), cancellationToken);

        var claimed = await database.TryClaimNextRunAsync(Now + 5000, cancellationToken);

        Assert.Equal("first", claimed!.Id);
        Assert.Equal(AnalysisStatuses.Running, claimed.Status);
        Assert.Equal(Now + 5000, claimed.StartedAtMs);
        // Only one pending run is left, so a second worker cannot take the same one.
        Assert.Equal(1, await database.CountPendingRunsAsync(cancellationToken));
        Assert.Equal("second", (await database.TryClaimNextRunAsync(Now + 6000, cancellationToken))!.Id);
        Assert.Null(await database.TryClaimNextRunAsync(Now + 7000, cancellationToken));
    }

    [Fact]
    public async Task A_run_the_service_lost_comes_back_interrupted_rather_than_replayed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await OpenAsync(cancellationToken);
        await database.TryInsertRunAsync(Run("queued", Now), cancellationToken);
        await database.TryInsertRunAsync(Run("started", Now + 1), cancellationToken);
        await database.TryClaimNextRunAsync(Now + 100, cancellationToken);

        var interrupted = await database.InterruptRunningRunsAsync(Now + 200, cancellationToken);

        Assert.Equal(2, interrupted);
        // Both the running one and the one that never started: a queued run the
        // service dropped is just as lost, and neither is replayed on its own.
        foreach (var id in new[] { "queued", "started" })
        {
            var run = await database.FindRunAsync(id, cancellationToken);
            Assert.Equal(AnalysisStatuses.Interrupted, run!.Status);
            Assert.Equal(Now + 200, run.FinishedAtMs);
        }
    }

    [Fact]
    public async Task Only_a_succeeded_run_past_its_lifetime_expires()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await OpenAsync(cancellationToken);
        await SeedCompletedAsync(database, "dead", AnalysisStatuses.Succeeded, Now - 1, cancellationToken);
        await SeedCompletedAsync(database, "alive", AnalysisStatuses.Succeeded, Now + 3_600_000, cancellationToken);
        await SeedCompletedAsync(database, "broken", AnalysisStatuses.Failed, null, cancellationToken);

        var expired = await database.ExpireRunsAsync(Now, cancellationToken);

        Assert.Equal("dead", Assert.Single(expired));
        Assert.Equal(AnalysisStatuses.Expired, (await database.FindRunAsync("dead", cancellationToken))!.Status);
        Assert.Equal(AnalysisStatuses.Succeeded, (await database.FindRunAsync("alive", cancellationToken))!.Status);
        Assert.Equal(AnalysisStatuses.Failed, (await database.FindRunAsync("broken", cancellationToken))!.Status);
        // The row survives its report: the most common next action is to run
        // the same parameters again.
        Assert.Equal(3, (await database.ListRunsAsync(50, cancellationToken)).Count);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
        File.Delete($"{_databasePath}-shm");
        File.Delete($"{_databasePath}-wal");
    }

    private async Task<HubDatabase> OpenAsync(CancellationToken cancellationToken)
    {
        var database = new HubDatabase(
            Options.Create(new HubOptions { DatabasePath = _databasePath }),
            TimeProvider.System);
        await database.InitializeAsync(cancellationToken);
        return database;
    }

    private static async Task SeedCompletedAsync(
        HubDatabase database,
        string id,
        string status,
        long? expiresAtMs,
        CancellationToken cancellationToken)
    {
        await database.TryInsertRunAsync(Run(id, Now), cancellationToken);
        await database.CompleteRunAsync(
            id, status, Now, expiresAtMs, "0.16.0", null, """{"empty":false}""", cancellationToken);
    }

    private static AnalysisRun Run(string id, long createdAtMs) => new(
        id, AnalysisStatuses.Pending, "prod-tempo", "Tempo, production", "production",
        SourceKinds.Tempo, """{"service":"orders","lookback":"1h"}""",
        "operator@example.internal", createdAtMs, null, null, null, null, null, null);
}
