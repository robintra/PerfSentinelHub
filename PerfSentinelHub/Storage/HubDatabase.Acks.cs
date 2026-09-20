using Microsoft.Data.Sqlite;
using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Storage;

public sealed partial class HubDatabase
{
    private const string AckReadUpsert = $"INSERT INTO ack_reads{ReadLedgerUpsert}";
    private const string AckReadSelect = $"{ReadLedgerSelect}ack_reads;";

    /// <summary>
    ///     Replaces one source's mirror with what its daemon just listed and
    ///     files the read, in one transaction. Replaced rather than merged: the
    ///     listing is the whole truth of that daemon, so an ack it no longer
    ///     lists was revoked or has expired. On a signature listed twice the
    ///     last row wins, and the daemon lists its baseline last, which is the
    ///     one its own lookup prefers.
    /// </summary>
    public async Task ReplaceSourceAcksAsync(
        string sourceId,
        IReadOnlyList<ParsedAck> acks,
        string state,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(false);
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM source_acks WHERE source_id = $source_id;";
                delete.Parameters.AddWithValue(SourceIdParameter, sourceId);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var ack in acks)
                await InsertSourceAckAsync(connection, transaction, sourceId, ack, observedAtMs, cancellationToken);
            await RecordReadAsync(
                connection,
                transaction,
                AckReadUpsert,
                sourceId,
                new SourceRead(observedAtMs, state, null),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task InsertSourceAckAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceId,
        ParsedAck ack,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              INSERT OR REPLACE INTO source_acks(
                                source_id, signature, origin, acked_by, reason, acked_at,
                                expires_at, expires_at_ms, read_at_ms)
                              VALUES (
                                $source_id, $signature, $origin, $acked_by, $reason, $acked_at,
                                $expires_at, $expires_at_ms, $observed_at);
                              """;
        command.Parameters.AddWithValue(SourceIdParameter, sourceId);
        command.Parameters.AddWithValue("$signature", ack.Signature);
        command.Parameters.AddWithValue("$origin", ack.Origin);
        command.Parameters.AddWithValue("$acked_by", ack.By);
        command.Parameters.AddWithValue("$reason", (object?)ack.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$acked_at", ack.At);
        command.Parameters.AddWithValue("$expires_at", (object?)ack.ExpiresAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$expires_at_ms", (object?)ack.ExpiresAtMs ?? DBNull.Value);
        command.Parameters.AddWithValue(ObservedAtParameter, observedAtMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     Files the outcome of an ack read that mirrored nothing. The rows of
    ///     the last read that succeeded stay, the state filed here says they are
    ///     no longer current, and the purge removes them once no read refreshes them.
    /// </summary>
    public Task RecordAckReadAsync(
        string sourceId,
        long readAtMs,
        string state,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        return RecordReadAsync(AckReadUpsert, sourceId, new SourceRead(readAtMs, state, errorCode), cancellationToken);
    }

    /// <summary>
    ///     The last ack read per source, keyed by source id. A source with no
    ///     row has never had its acks read.
    /// </summary>
    public Task<Dictionary<string, SourceRead>> QueryAckReadsAsync(CancellationToken cancellationToken)
    {
        return QueryReadsAsync(AckReadSelect, cancellationToken);
    }
}
