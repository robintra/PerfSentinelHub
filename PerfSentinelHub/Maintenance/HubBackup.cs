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
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var vacuum = connection.CreateCommand();
            // VACUUM INTO accepts any expression, so the destination binds as
            // a parameter and needs no escaping. Busy retries ride the
            // provider's CommandTimeout (30 s default).
            vacuum.CommandText = "VACUUM INTO $destination;";
            vacuum.Parameters.AddWithValue("$destination", destinationPath);
            await vacuum.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception)
        {
            Console.Error.WriteLine($"Backup failed: {exception.Message}");
            // SQLite never unlinks a partial output, and a leftover would
            // make the overwrite guard refuse every retry at this path.
            TryDeletePartial(destinationPath);
            return 1;
        }

        Console.WriteLine($"Backup written to '{destinationPath}'.");
        return 0;
    }

    private static void TryDeletePartial(string destinationPath)
    {
        try
        {
            File.Delete(destinationPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
