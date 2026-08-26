using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Maintenance;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-backup-{Guid.NewGuid():N}.db");

    private readonly string _backupPath = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-backup-copy-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Backup_produces_an_openable_copy_with_the_same_findings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedDatabaseAsync(cancellationToken);

        var exitCode = await HubBackup.RunAsync(_databasePath, _backupPath, cancellationToken);

        Assert.Equal(0, exitCode);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _backupPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM findings;";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync(cancellationToken))!);
    }

    [Fact]
    public async Task Backup_refuses_an_existing_destination()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedDatabaseAsync(cancellationToken);
        await File.WriteAllTextAsync(_backupPath, "keep", cancellationToken);

        var exitCode = await HubBackup.RunAsync(_databasePath, _backupPath, cancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal("keep", await File.ReadAllTextAsync(_backupPath, cancellationToken));
    }

    [Fact]
    public async Task Backup_reports_a_missing_database()
    {
        Assert.Equal(
            1,
            await HubBackup.RunAsync(_databasePath, _backupPath, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(_backupPath));
    }

    [Fact]
    public async Task Backup_reports_a_failed_vacuum_and_leaves_no_partial_file()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedDatabaseAsync(cancellationToken);
        var unreachable = Path.Combine(
            Path.GetTempPath(),
            $"perf-sentinel-hub-no-such-dir-{Guid.NewGuid():N}",
            "backup.db");

        var exitCode = await HubBackup.RunAsync(_databasePath, unreachable, cancellationToken);

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(unreachable));
    }

    private async Task SeedDatabaseAsync(CancellationToken cancellationToken)
    {
        using var database = new HubDatabase(
            Options.Create(new HubOptions { DatabasePath = _databasePath }),
            TimeProvider.System);
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "daemon-findings-0.11.2.json"),
            cancellationToken));
        await database.UpsertBatchAsync(
            new SourceSnapshot("production-a", "Production A", "production", "0.11.2"),
            batch,
            1786190000000,
            cancellationToken);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
        File.Delete($"{_databasePath}-shm");
        File.Delete($"{_databasePath}-wal");
        File.Delete(_backupPath);
    }
}
