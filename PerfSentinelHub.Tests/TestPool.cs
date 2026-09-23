using Microsoft.Data.Sqlite;

namespace PerfSentinelHub.Tests;

internal static class TestPool
{
    /// <summary>
    ///     Releases the pooled handles of one test database. Test classes run in parallel, and
    ///     <c>SqliteConnection.ClearAllPools()</c> disposes a handle another class has just been handed.
    /// </summary>
    internal static void ClearFor(string databasePath)
    {
        // Pools are keyed by the exact connection string, so each shape the suite opens a file
        // with has its own pool: HubDatabase.OpenConnectionAsync, the read-only HubBackup and
        // its checks, and a bare seed. A pool left behind keeps the file open, which Linux
        // tolerates on delete and Windows refuses.
        string[] connectionStrings =
        [
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true
            }.ToString(),
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString(),
            $"Data Source={databasePath}"
        ];
        foreach (var connectionString in connectionStrings)
        {
            using var own = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(own);
        }
    }
}
