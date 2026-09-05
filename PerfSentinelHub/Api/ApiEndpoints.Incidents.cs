using System.Globalization;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    // A reload loop, or five people watching the same screen, must not storm the
    // fleet: a source whose ring was read this recently is left alone and its
    // stored copy is served instead. A constant rather than a setting, because
    // the floor guards the daemons against this Hub and is not the operator's to
    // raise, and because a screen open is not a tuning decision.
    private const long RefreshDebounceMs = 10_000;

    /// <summary>
    ///     Reads every daemon's incidents ring now, then answers with the same
    ///     listing as the GET so the screen needs one round trip. A POST because
    ///     it writes to the store, and because a GET would be cached and
    ///     prefetched, which is the one thing a fleet read must not be.
    /// </summary>
    private static async Task RefreshIncidentsAsync(
        HttpContext context,
        HubDatabase database,
        IncidentReader reader,
        IncidentRefreshGate gate,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!TryParseIncidentQuery(context.Request, options.Value, out var query))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!gate.TryEnter())
        {
            context.Response.Headers.RetryAfter = "1";
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        try
        {
            // The fan-out runs inside the request an operator is waiting on and
            // holds one of two gate slots, so it gets a deadline of its own: one
            // hung daemon costs a slow read, never a screen that never paints
            // and a slot nobody else can take.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.Value.HttpTimeout * 3);
            await ReadFleetAsync(database, reader, options.Value, timeProvider, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Whatever landed before the deadline is in the store, and the
            // listing below answers with it, the contract a partially read
            // fleet already has.
        }
        finally
        {
            gate.Exit();
        }

        var rows = await database.ListIncidentsAsync(query, cancellationToken);
        await IncidentWriter.WriteArrayAsync(context.Response, rows, options.Value.Sources, cancellationToken);
    }

    /// <summary>
    ///     Every daemon at once, bounded the way the poll worker is. A source
    ///     that refuses, has no such route or fails is left with its own
    ///     incident_reads state and never touches source_state, so one daemon's
    ///     401 costs the others nothing.
    /// </summary>
    private static async Task ReadFleetAsync(
        HubDatabase database,
        IncidentReader reader,
        HubOptions options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var readAtMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var reads = await database.QueryIncidentReadsAsync(cancellationToken);
        using var concurrency = new SemaphoreSlim(options.MaxConcurrentPolls);
        var tasks = new List<Task>(options.Sources.Count);
        // A query would capture the disposable semaphore and obscure its lifetime.
        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var source in options.Sources)
        {
            // A trace backend serves no incidents route, the same reason the
            // poll worker skips one.
            if (source.Kind != SourceKinds.Daemon)
                continue;
            // Bounded below as well: an NTP correction that steps the clock back
            // makes the age negative, and an unbounded test would then skip that
            // source on every refresh while the screen prints it as freshly read.
            if (reads.TryGetValue(source.Id, out var read)
                && readAtMs - read.LastReadMs is >= 0 and < RefreshDebounceMs)
                continue;
            tasks.Add(ReadOneAsync(reader, source, readAtMs, concurrency, cancellationToken));
        }

        await Task.WhenAll(tasks);
    }

    private static async Task ReadOneAsync(
        IncidentReader reader,
        SourceOptions source,
        long readAtMs,
        SemaphoreSlim concurrency,
        CancellationToken cancellationToken)
    {
        await concurrency.WaitAsync(cancellationToken);
        try
        {
            await reader.ReadAsync(source, readAtMs, cancellationToken);
        }
        finally
        {
            concurrency.Release();
        }
    }

    private static async Task GetIncidentsAsync(
        HttpContext context,
        HubDatabase database,
        IOptions<HubOptions> options,
        CancellationToken cancellationToken)
    {
        if (!TryParseIncidentQuery(context.Request, options.Value, out var query))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var rows = await database.ListIncidentsAsync(query, cancellationToken);
        await IncidentWriter.WriteArrayAsync(context.Response, rows, options.Value.Sources, cancellationToken);
    }

    private static async Task GetIncidentAsync(
        string id,
        HttpContext context,
        HubDatabase database,
        IOptions<HubOptions> options,
        CancellationToken cancellationToken)
    {
        var row = await database.FindIncidentAsync(id, cancellationToken);
        if (row is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await IncidentWriter.WriteObjectAsync(context.Response, row, options.Value.Sources, cancellationToken);
    }

    private static bool TryParseIncidentQuery(HttpRequest request, HubOptions options, out IncidentQuery query)
    {
        query = null!;
        if (!HasValidUtf8(request.QueryString.Value) || request.Query.Any(item => item.Value.Count != 1))
            return false;
        if (!TryReadBounded(request, "limit", options.DefaultReadLimit, 1, options.MaxReadLimit, out var limit) ||
            !TryReadBounded(request, "offset", 0, 0, int.MaxValue, out var offset) ||
            !TryReadIncidentFilters(request, options, out var kind, out var sourceIds))
            return false;

        query = new IncidentQuery(
            Service: ReadOptional(request, "service"),
            Namespace: ReadOptional(request, "namespace"),
            Kind: kind,
            SourceIds: sourceIds,
            Offset: offset,
            Limit: limit);
        return true;
    }

    /// <summary>
    ///     The closed filters. A kind, a source id or an environment is
    ///     configuration, so an unknown one is a bad request rather than an empty
    ///     page: a typo would otherwise read as "no incidents". Given together,
    ///     source_id wins over environment as the narrower of the two.
    /// </summary>
    private static bool TryReadIncidentFilters(
        HttpRequest request,
        HubOptions options,
        out string? kind,
        out IReadOnlyList<string>? sourceIds)
    {
        sourceIds = null;
        kind = ReadOptional(request, "kind");
        if (kind is not null && Array.IndexOf(IncidentParser.Kinds, kind) < 0)
            return false;

        var sourceId = ReadOptional(request, "source_id");
        if (sourceId is not null &&
            !options.Sources.Any(source => string.Equals(source.Id, sourceId, StringComparison.Ordinal)))
            return false;

        var environment = ReadOptional(request, "environment");
        if (environment is not null)
        {
            sourceIds = options.Sources
                .Where(source => string.Equals(source.Environment, environment, StringComparison.Ordinal))
                .Select(source => source.Id)
                .ToArray();
            if (sourceIds.Count == 0)
                return false;
        }

        if (sourceId is not null)
            sourceIds = [sourceId];
        return true;
    }

    private static bool TryReadBounded(
        HttpRequest request,
        string name,
        int fallback,
        int min,
        int max,
        out int value)
    {
        value = fallback;
        if (!request.Query.TryGetValue(name, out var raw))
            return true;
        return int.TryParse(raw[0], NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
               value >= min && value <= max;
    }
}

/// <summary>
///     Each refresh reads the whole daemon fleet, and every page it pulls is
///     buffered under the client's 4 MiB cap, so a burst of screen opens is real
///     memory and real load on the daemons rather than a few cheap requests.
/// </summary>
public sealed class IncidentRefreshGate() : RequestGate(MaxReads)
{
    // Public because IncidentRefreshApiTests pins it, the same reason
    // DaemonViewGate.MaxReads is: the cap is a contract, not a detail.
    public const int MaxReads = 2;
}
