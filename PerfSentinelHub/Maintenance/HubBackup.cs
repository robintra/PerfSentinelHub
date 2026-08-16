using Microsoft.Data.Sqlite;

namespace PerfSentinelHub.Maintenance;

/// <summary>
/// Copies the SQLite database into a fresh file with VACUUM INTO. Safe against
/// a live Hub: WAL allows this read-only snapshot next to the single writer.
/// </summary>
public static class HubBackup
{
    public static async Task<int> RunAsync(
        string databasePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath))
        {
            Console.Error.WriteLine($"No database at '{databasePath}'.");
            return 1;
        }

        if (File.Exists(destinationPath))
        {
            Console.Error.WriteLine($"Refusing to overwrite existing '{destinationPath}'.");
            return 1;
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 5000;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var vacuum = connection.CreateCommand();
        // VACUUM INTO accepts any expression, so the destination binds as a
        // parameter and needs no escaping.
        vacuum.CommandText = "VACUUM INTO $destination;";
        vacuum.Parameters.AddWithValue("$destination", destinationPath);
        try
        {
            await vacuum.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception)
        {
            Console.Error.WriteLine($"Backup failed: {exception.Message}");
            return 1;
        }

        Console.WriteLine($"Backup written to '{destinationPath}'.");
        return 0;
    }
}
