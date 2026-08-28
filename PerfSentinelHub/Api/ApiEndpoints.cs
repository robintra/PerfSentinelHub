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
    public static void MapHubApi(this WebApplication app)
    {
        var version = typeof(ApiEndpoints).Assembly.GetName().Version?.ToString() ?? "unknown";

        app.MapGet("/api/status", async (
            HttpRequest request,
            EngineProbe engine,
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
                await database.CountPendingRunsAsync(cancellationToken),
                analysis.Workers,
                new StatusLimits(
                    analysis.MaxTracesCap,
                    (int)analysis.Timeout.TotalSeconds,
                    (int)analysis.ReportRetention.TotalHours,
                    analysis.MaxTracesEmbedded),
                [.. DetectionOverrides.Schema
                    .Select(knob => new DetectionKnob(knob.Name, knob.Min, knob.Max, knob.Default))]);
        });
        app.MapGet("/api/sources", GetSourcesAsync);
        app.MapGet("/api/findings", GetFindingsAsync);
        app.MapGet("/api/findings/{traceId}", GetFindingsByTraceAsync);
        app.MapPost("/api/import/findings", ImportFindingsAsync);
        app.MapGet("/health/live", TypedResults.Ok);
        app.MapGet("/health/ready", (HubDatabase database) =>
            database.IsReady ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
    }

    private static async Task<IReadOnlyList<SourceResponse>> GetSourcesAsync(
        HubDatabase database,
        IOptions<HubOptions> options,
        CancellationToken cancellationToken)
    {
        var states = await database.QuerySourceStatesAsync(cancellationToken);
        return [.. options.Value.Sources.Select(source =>
        {
            states.TryGetValue(source.Id, out var state);
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
                state?.LastErrorCode);
        })];
    }

    private static async Task<IResult> ImportFindingsAsync(
        HttpRequest request,
        HubDatabase database,
        ImportGate gate,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!HasValidUtf8(request.QueryString.Value) ||
            request.Query.Count != 1 ||
            !request.Query.TryGetValue("source_id", out var sourceIds) ||
            sourceIds.Count != 1)
            return TypedResults.BadRequest();
        var sourceId = sourceIds[0];
        if (string.IsNullOrEmpty(sourceId))
            return TypedResults.BadRequest();
        var source = options.Value.Sources.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, sourceId, StringComparison.Ordinal));
        if (source is null || !IsAuthorized(request, source.ImportApiKey))
            return TypedResults.Unauthorized();
        if (!gate.TryEnter())
        {
            request.HttpContext.Response.Headers.RetryAfter = "1";
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            var payload = await ReadBodyAsync(request, cancellationToken);
            if (payload is null)
                return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);

            ParsedImport import;
            try
            {
                import = FindingParser.ParseImport(payload);
            }
            catch (ImportBatchTooLargeException)
            {
                return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            catch (InvalidDataException)
            {
                return TypedResults.BadRequest();
            }

            if (import.Batch.Findings.Count == 0)
                return TypedResults.BadRequest();
            var stored = await database.TryUpsertBatchAsync(
                new SourceSnapshot(source.Id, source.Name, source.Environment, import.ProducerVersion),
                import.Batch,
                timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                cancellationToken);
            if (!stored)
            {
                request.HttpContext.Response.Headers.RetryAfter = "1";
                return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
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
            gate.Exit();
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

        var limit = options.DefaultReadLimit;
        if (request.Query.TryGetValue("limit", out var rawLimit) &&
            (!int.TryParse(rawLimit[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit) ||
             limit < 1 || limit > options.MaxReadLimit))
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
            status);
        return true;
    }

    private static string? ReadOptional(HttpRequest request, string name) =>
        request.Query.TryGetValue(name, out var value) ? value[0] : null;

    private static bool HasValidUtf8(string? rawQuery)
    {
        if (string.IsNullOrEmpty(rawQuery))
            return true;

        var bytes = new List<byte>(rawQuery.Length);
        var index = 0;
        while (index < rawQuery.Length)
        {
            if (rawQuery[index] == '%')
            {
                if (index + 2 >= rawQuery.Length ||
                    !byte.TryParse(rawQuery.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                    return false;
                bytes.Add(value);
                index += 3;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(rawQuery[index].ToString()));
                index++;
            }
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
public sealed class ImportGate : IDisposable
{
    public const int MaxImports = 4;

    private readonly SemaphoreSlim _gate = new(MaxImports, MaxImports);

    public bool TryEnter() => _gate.Wait(0);

    public void Exit() => _gate.Release();

    public void Dispose() => _gate.Dispose();
}
