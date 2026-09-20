using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    // A deep OFFSET is a scan: SQLite steps over every skipped row.
    private const int MaxFindingOffset = 1_000_000;

    public static void MapHubApi(this WebApplication app)
    {
        var version = HubVersion.Current;

        app.MapGet("/api/status", async (
            HttpRequest request,
            EngineProbe engine,
            UpdateChecker updates,
            HubDatabase database,
            IOptions<HubOptions> hubOptions,
            CancellationToken cancellationToken) =>
        {
            var analysis = hubOptions.Value.Analysis;
            return new StatusResponse(
                "perf-sentinel-hub",
                version,
                KnownIdentity(request, analysis),
                engine.Version,
                updates.LatestEngineVersion,
                updates.LatestHubVersion,
                await database.CountPendingRunsAsync(cancellationToken),
                analysis.Workers,
                new StatusLimits(
                    analysis.MaxTracesCap,
                    (int)analysis.Timeout.TotalSeconds,
                    (int)analysis.ReportRetention.TotalHours,
                    analysis.MaxTracesEmbedded,
                    hubOptions.Value.MaxReadLimit),
                [.. DetectionOverrides.SchemaFor(engine.Version)]);
        });
        app.MapGet("/api/sources", GetSourcesAsync);
        app.MapGet("/api/sources/{sourceId}/daemon", GetDaemonViewAsync);
        // AllowAnonymous is inert without Hub:Auth. With it, these are the routes a
        // machine calls: IDE plugins and CI read findings, a daemon pushes with its key.
        app.MapGet("/api/findings", GetFindingsAsync).AllowAnonymous();
        app.MapGet("/api/findings/{traceId}", GetFindingsByTraceAsync).AllowAnonymous();
        app.MapGet("/api/incidents", GetIncidentsAsync);
        app.MapGet("/api/incidents/{id}", GetIncidentAsync);
        app.MapPost("/api/incidents/refresh", RefreshIncidentsAsync);
        app.MapPost("/api/import/findings", ImportFindingsAsync).AllowAnonymous();
        app.MapGet("/health/live", TypedResults.Ok).AllowAnonymous();
        app.MapGet("/health/ready", (HubDatabase database) =>
                database.IsReady ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
            .AllowAnonymous();
    }

    private static async Task<IReadOnlyList<SourceResponse>> GetSourcesAsync(
        HubDatabase database,
        IOptions<HubOptions> options,
        CancellationToken cancellationToken)
    {
        var states = await database.QuerySourceStatesAsync(cancellationToken);
        var reads = await database.QueryIncidentReadsAsync(cancellationToken);
        return
        [
            .. options.Value.Sources.Select(source =>
            {
                states.TryGetValue(source.Id, out var state);
                reads.TryGetValue(source.Id, out var read);
                return new SourceResponse(
                    source.Id,
                    source.Name,
                    source.Environment,
                    source.Kind,
                    source.RetentionHours,
                    // A source that has never failed is reachable, including one
                    // that has never been observed at all: the Hub has no evidence
                    // against it, and a trace backend is never polled.
                    state?.UnreachableSinceMs is null,
                    state?.LastAttemptMs,
                    state?.LastSuccessMs,
                    state?.UnreachableSinceMs,
                    state?.ProducerVersion,
                    state?.LastErrorCode,
                    source.PublicEndpointArgument,
                    source.EngineSubcommand,
                    source.PublishedAuthHeaderName,
                    read?.State,
                    read?.LastReadMs);
            })
        ];
    }

    private static async Task<IResult> ImportFindingsAsync(
        HttpRequest request,
        HubDatabase database,
        ImportAdmission admission,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!HasValidUtf8(request.QueryString.Value) ||
            request.Query.Count != 1 ||
            !request.Query.TryGetValue("source_id", out var sourceIds) ||
            sourceIds.Count != 1)
            return admission.Refuse(ImportRejection.BadRequest, TypedResults.BadRequest());
        var sourceId = sourceIds[0];
        if (string.IsNullOrEmpty(sourceId))
            return admission.Refuse(ImportRejection.BadRequest, TypedResults.BadRequest());
        var source = options.Value.Sources.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, sourceId, StringComparison.Ordinal));
        if (source is null || !IsAuthorized(request, source.ImportApiKey))
            return admission.Refuse(ImportRejection.Unauthorized, TypedResults.Unauthorized());
        if (!admission.Gate.TryEnter())
        {
            request.HttpContext.Response.Headers.RetryAfter = "1";
            return admission.Refuse(ImportRejection.GateFull,
                TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }

        try
        {
            var payload = await ReadBodyAsync(request, cancellationToken);
            if (payload is null)
                return admission.Refuse(ImportRejection.TooLarge,
                    TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge));

            ParsedImport import;
            try
            {
                import = FindingParser.ParseImport(payload);
            }
            catch (ImportBatchTooLargeException)
            {
                return admission.Refuse(ImportRejection.TooLarge,
                    TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge));
            }
            catch (InvalidDataException)
            {
                return admission.Refuse(ImportRejection.BadRequest, TypedResults.BadRequest());
            }

            if (import.Batch.Findings.Count == 0)
                return admission.Refuse(ImportRejection.BadRequest, TypedResults.BadRequest());
            var stored = await database.TryUpsertBatchAsync(
                new SourceSnapshot(source.Id, source.Name, source.Environment, import.ProducerVersion),
                import.Batch,
                timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                cancellationToken);
            if (!stored)
            {
                request.HttpContext.Response.Headers.RetryAfter = "1";
                return admission.Refuse(ImportRejection.WriteTimeout,
                    TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable));
            }

            if (import.Batch.RejectedCount > 0)
                LogRejectedImportedFindings(
                    loggerFactory.CreateLogger("ImportApi"),
                    source.Id,
                    import.Batch.RejectedCount);
            return TypedResults.Ok(new ImportResponse(import.Batch.Findings.Count, import.Batch.RejectedCount));
        }
        finally
        {
            admission.Gate.Exit();
        }
    }

    private static async Task GetFindingsAsync(
        HttpContext context,
        HubDatabase database,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!TryParseQuery(context.Request, options.Value, out var query))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var rows = await database.QueryFindingsAsync(query, cancellationToken);
        await FindingEnvelopeWriter.WriteArrayAsync(
            context.Response,
            rows,
            timeProvider.GetUtcNow(),
            loggerFactory.CreateLogger("FindingsApi"),
            cancellationToken);
    }

    private static async Task GetFindingsByTraceAsync(
        string traceId,
        HttpResponse response,
        HubDatabase database,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var rows = await database.FindByTraceAsync(traceId, cancellationToken);
        await FindingEnvelopeWriter.WriteArrayAsync(
            response,
            rows,
            timeProvider.GetUtcNow(),
            loggerFactory.CreateLogger("FindingsApi"),
            cancellationToken);
    }

    private static bool TryParseQuery(HttpRequest request, HubOptions options, out FindingQuery query)
    {
        query = null!;
        if (!HasValidUtf8(request.QueryString.Value) || request.Query.Any(item => item.Value.Count != 1))
            return false;

        if (!TryReadBounded(request, "limit", options.DefaultReadLimit, 1, options.MaxReadLimit, out var limit) ||
            !TryReadBounded(request, "offset", 0, 0, MaxFindingOffset, out var offset) ||
            !TryReadSourceScope(request, options, out var sourceIds) ||
            !TryReadEpochMs(request, "from", out var fromMs) ||
            !TryReadEpochMs(request, "to", out var toMs) ||
            fromMs > toMs)
            return false;

        var includeAcked = true;
        if (request.Query.TryGetValue("include_acked", out var rawIncludeAcked) &&
            !bool.TryParse(rawIncludeAcked[0], out includeAcked))
            return false;

        var status = ReadOptional(request, "status");
        if (status is not null and not ("active" or "likely_resolved" or "not_observed"))
            return false;

        query = new FindingQuery(
            ReadOptional(request, "service"),
            ReadOptional(request, "finding_type"),
            ReadOptional(request, "severity"),
            limit,
            includeAcked,
            status,
            offset,
            sourceIds,
            fromMs,
            toMs);
        return true;
    }

    /// <summary>
    ///     The source scope the findings and the incidents reads share, null for
    ///     the whole fleet. A source id or an environment is configuration, so an
    ///     unknown one is a bad request rather than an empty page: a typo would
    ///     otherwise read as "nothing there". Given together they intersect, and a
    ///     source outside the named environment leaves the empty set, since a
    ///     screen offers the two as filters that both apply.
    /// </summary>
    private static bool TryReadSourceScope(
        HttpRequest request,
        HubOptions options,
        out IReadOnlyList<string>? sourceIds)
    {
        sourceIds = null;
        var sourceId = ReadOptional(request, "source_id");
        if (sourceId is not null &&
            !options.Sources.Any(source => string.Equals(source.Id, sourceId, StringComparison.Ordinal)))
            return false;

        var environment = ReadOptional(request, "environment");
        if (environment is not null)
        {
            sourceIds =
            [
                .. options.Sources
                    .Where(source => string.Equals(source.Environment, environment, StringComparison.Ordinal))
                    .Select(source => source.Id)
            ];
            if (sourceIds.Count == 0)
                return false;
        }

        if (sourceId is not null)
            sourceIds = sourceIds is null || sourceIds.Contains(sourceId) ? [sourceId] : [];
        return true;
    }

    // Null when absent or blank. No sign and no padding: a dashboard passes its
    // time range as plain epoch milliseconds, and anything else is a mistake.
    private static bool TryReadEpochMs(HttpRequest request, string name, out long? value)
    {
        value = null;
        var raw = ReadOptional(request, name);
        if (raw is null)
            return true;
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            return false;
        value = parsed;
        return true;
    }

    // A blank value reads as absent: Grafana sends a single space for its All
    // choice, which would otherwise empty the page of a free filter and answer
    // 400 on a closed set.
    private static string? ReadOptional(HttpRequest request, string name)
    {
        return request.Query.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value[0])
            ? value[0]
            : null;
    }

    private static bool HasValidUtf8(string? rawQuery)
    {
        if (string.IsNullOrEmpty(rawQuery))
            return true;

        var bytes = new List<byte>(rawQuery.Length);
        var index = 0;
        while (index < rawQuery.Length)
            if (rawQuery[index] == '%')
            {
                if (index + 2 >= rawQuery.Length ||
                    !byte.TryParse(rawQuery.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                        out var value))
                    return false;
                bytes.Add(value);
                index += 3;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(rawQuery[index].ToString()));
                index++;
            }

        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes.ToArray());
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsAuthorized(HttpRequest request, string? apiKey)
    {
        if (apiKey is null)
            return false;
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(request.Headers["X-API-Key"].ToString()));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static async Task<byte[]?> ReadBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        const int maxBodyBytes = 2 * 1024 * 1024;
        if (request.ContentLength > maxBodyBytes)
            return null;

        using var body = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return body.ToArray();
            if (body.Length + read > maxBodyBytes)
                return null;
            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    [LoggerMessage(1301, LogLevel.Warning, "Source {SourceId} rejected {RejectedCount} imported findings.")]
    private static partial void LogRejectedImportedFindings(
        ILogger logger,
        string sourceId,
        int rejectedCount);
}

// Bounds how many import bodies may be buffered at once (MaxImports * 2 MiB). It is not a write
// lock -- HubDatabase serializes writes -- so one slow uploader must not stall the whole fleet.
public sealed class ImportGate() : RequestGate(MaxImports)
{
    public const int MaxImports = 4;
}
