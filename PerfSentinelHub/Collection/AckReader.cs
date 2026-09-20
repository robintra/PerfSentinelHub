using Microsoft.Data.Sqlite;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Collection;

/// <summary>
///     One daemon's active acknowledgments, read and mirrored. Read-only: the
///     daemon stays the owner of its acks and the Hub keeps a copy of what it
///     last listed.
/// </summary>
/// <remarks>
///     Its failures are its own, for the reason IncidentReader gives: a refused
///     key or a missing route says nothing about whether the daemon answers. So
///     nothing here reaches MarkSourceFailureAsync, the outcome is filed in
///     ack_reads. A storage failure is the Hub's own and is left to the caller.
/// </remarks>
public sealed partial class AckReader(
    DaemonClient client,
    HubDatabase database,
    ILogger<AckReader> logger)
{
    // The daemon's own cap on the listing, which it does not page. A page
    // that reaches it may have lost its tail, and the baseline is listed last.
    public const int AcksCap = 1000;

    // The first daemon whose listing takes include_toml. An older one ignores
    // the parameter and lists its runtime acks alone, and that answer cannot
    // be told from a daemon with no baseline, so it is never asked.
    private static readonly Version ListsItsBaselineSince = new(0, 24, 0);

    /// <summary>Reads the daemon's listing and mirrors it, or files why it could not.</summary>
    public async Task ReadAsync(
        SourceOptions source,
        string producerVersion,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = ListsItsBaseline(producerVersion)
                ? await client.FetchAcksAsync(source, cancellationToken)
                : null;
            if (payload is null)
            {
                await database.RecordAckReadAsync(
                    source.Id, observedAtMs, AckReadStates.Absent, null, cancellationToken);
                return;
            }

            await MirrorAsync(source, AckParser.Parse(payload), observedAtMs, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AcksUnauthorizedException)
        {
            await database.RecordAckReadAsync(
                source.Id, observedAtMs, AckReadStates.Unauthorized, null, cancellationToken);
            LogAcksUnauthorized(logger, source.Id);
        }
        catch (Exception exception) when (exception is not SqliteException)
        {
            // The shared switch names the findings leg for a malformed body, and
            // this leg has its own parser.
            var errorCode = exception is InvalidDataException
                ? "invalid_acks"
                : SourcePoller.ErrorCode(exception);
            await database.RecordAckReadAsync(
                source.Id, observedAtMs, AckReadStates.Error, errorCode, cancellationToken);
            LogAcksFailed(logger, source.Id, errorCode);
        }
    }

    private async Task MirrorAsync(
        SourceOptions source,
        ParsedAckPage page,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        if (page.RejectedCount > 0)
            LogRejectedAcks(logger, source.Id, page.RejectedCount);
        var isTruncated = page.Acks.Count + page.RejectedCount >= AcksCap;
        if (isTruncated)
            LogAcksTruncated(logger, source.Id, AcksCap);
        await database.ReplaceSourceAcksAsync(
            source.Id,
            page.Acks,
            isTruncated ? AckReadStates.Truncated : AckReadStates.Ok,
            observedAtMs,
            cancellationToken);
    }

    // A pre-release of the floor lists its baseline already, so the suffix is
    // dropped. A version that does not parse is read as an old daemon.
    private static bool ListsItsBaseline(string producerVersion)
    {
        var suffix = producerVersion.IndexOf('-');
        return Version.TryParse(suffix < 0 ? producerVersion : producerVersion[..suffix], out var version) &&
               version >= ListsItsBaselineSince;
    }

    [LoggerMessage(1108, LogLevel.Warning, "Source {SourceId} rejected {RejectedCount} acks.")]
    private static partial void LogRejectedAcks(ILogger logger, string sourceId, int rejectedCount);

    [LoggerMessage(
        1109,
        LogLevel.Warning,
        "Source {SourceId} refused the Hub's key on /api/acks; its findings were still collected.")]
    private static partial void LogAcksUnauthorized(ILogger logger, string sourceId);

    [LoggerMessage(
        1110,
        LogLevel.Warning,
        "Source {SourceId} acks read failed: {ErrorCode}; its findings were still collected.")]
    private static partial void LogAcksFailed(ILogger logger, string sourceId, string errorCode);

    [LoggerMessage(
        1111,
        LogLevel.Warning,
        "Source {SourceId} lists {Cap} acks or more, the daemon's cap; its listing is possibly truncated.")]
    private static partial void LogAcksTruncated(ILogger logger, string sourceId, int cap);
}
