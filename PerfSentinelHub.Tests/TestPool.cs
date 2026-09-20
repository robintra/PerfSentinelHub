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
        // Pools are keyed by connection string, so this mirrors HubDatabase.OpenConnectionAsync.
        using var own = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString());
        SqliteConnection.ClearPool(own);
    }
}
