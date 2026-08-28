using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    private const int DefaultRunLimit = 50;
    private const int MaxRunLimit = 500;
    private const int RunIdHexChars = 16;
    private const int MaxIdentityChars = 254;

    public static void MapAnalysisApi(this WebApplication app)
    {
        app.MapPost("/api/analyses", SubmitAnalysisAsync);
        app.MapGet("/api/analyses", ListAnalysesAsync);
        app.MapGet("/api/analyses/{id}", GetAnalysisAsync);
        app.MapGet("/reports/{id}.html", GetReportAsync);
    }

    private static async Task<IResult> SubmitAnalysisAsync(
        HttpRequest request,
        HubDatabase database,
        AnalysisRunner runner,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var hubOptions = options.Value;
        if (hubOptions.Analysis.EngineBinaryPath is null)
            return Problem(StatusCodes.Status503ServiceUnavailable,
                "No analysis engine is configured on this Hub.");

        var payload = await ReadBodyAsync(request, cancellationToken);
        if (payload is null)
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return Problem(StatusCodes.Status400BadRequest, "The body is not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (ReadSource(root, hubOptions) is not { } source)
                return Problem(StatusCodes.Status400BadRequest, "Unknown source_id.");

            var requestElement = root.TryGetProperty("request", out var submitted)
                ? submitted
                : default;
            var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            if (AnalysisRequest.TryParse(requestElement, source, hubOptions.Analysis, nowMs, out var error) is null)
                return Problem(StatusCodes.Status400BadRequest, error ?? "The request is invalid.");

            var run = NewRun(source, requestElement, Identity(request, hubOptions.Analysis), nowMs);
            await database.InsertRunAsync(run, cancellationToken);
            return TypedResults.Accepted($"/api/analyses/{run.Id}", new SubmittedAnalysis(run.Id, run.Status));
        }
    }

    private static async Task ListAnalysesAsync(
        HttpContext context,
        HubDatabase database,
        CancellationToken cancellationToken)
    {
        var limit = DefaultRunLimit;
        if (context.Request.Query.TryGetValue("limit", out var raw) &&
            (raw.Count != 1 ||
             !int.TryParse(raw[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit) ||
             limit < 1 || limit > MaxRunLimit))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var runs = await database.ListRunsAsync(limit, cancellationToken);
        await AnalysisRunWriter.WriteArrayAsync(context.Response, runs, cancellationToken);
    }

    private static async Task<IResult> GetAnalysisAsync(
        string id,
        HttpContext context,
        HubDatabase database,
        CancellationToken cancellationToken)
    {
        if (!IsRunId(id))
            return TypedResults.NotFound();
        var run = await database.FindRunAsync(id, cancellationToken);
        if (run is null)
            return TypedResults.NotFound();

        await AnalysisRunWriter.WriteObjectAsync(context.Response, run, cancellationToken);
        return TypedResults.Empty;
    }

    /// <summary>
    /// Serves a rendered report from the same origin as the launcher, which is
    /// what lets the dashboard pick up the theme with no URL parameter.
    /// </summary>
    private static async Task<IResult> GetReportAsync(
        string id,
        HubDatabase database,
        AnalysisRunner runner,
        CancellationToken cancellationToken)
    {
        // The id is the only path segment that reaches the filesystem, so it is
        // checked against a hex vocabulary rather than sanitised.
        if (!IsRunId(id))
            return TypedResults.NotFound();

        var run = await database.FindRunAsync(id, cancellationToken);
        if (run?.Status != AnalysisStatuses.Succeeded)
            return TypedResults.NotFound();

        var path = runner.ReportPath(id);
        return File.Exists(path)
            ? TypedResults.PhysicalFile(path, "text/html; charset=utf-8")
            : TypedResults.NotFound();
    }

    private static AnalysisRun NewRun(
        SourceOptions source,
        JsonElement request,
        string requestedBy,
        long nowMs) => new(
            NewRunId(),
            AnalysisStatuses.Pending,
            source.Id,
            source.Name,
            source.Environment,
            source.Kind,
            request.ValueKind == JsonValueKind.Undefined ? "{}" : request.GetRawText(),
            requestedBy,
            nowMs,
            null, null, null, null, null, null);

    private static SourceOptions? ReadSource(JsonElement root, HubOptions options) =>
        root.TryGetProperty("source_id", out var sourceId) &&
        sourceId.ValueKind == JsonValueKind.String
            ? options.Sources.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, sourceId.GetString(), StringComparison.Ordinal))
            : null;

    /// <summary>
    /// The identity a reverse proxy established upstream. The Hub has no
    /// account surface and does not verify it, so it is recorded as a claim.
    /// </summary>
    private static string Identity(HttpRequest request, AnalysisOptions analysis) =>
        KnownIdentity(request, analysis) ?? "unknown";

    /// <summary>Null when no proxy established one, rather than a placeholder.</summary>
    internal static string? KnownIdentity(HttpRequest request, AnalysisOptions analysis)
    {
        if (!request.Headers.TryGetValue(analysis.IdentityHeader, out var values) ||
            values.Count != 1 ||
            values[0] is not { Length: > 0 } identity)
            return null;

        var trimmed = identity.Length > MaxIdentityChars ? identity[..MaxIdentityChars] : identity;
        return trimmed.Any(char.IsControl) ? null : trimmed;
    }

    private static string NewRunId() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(RunIdHexChars / 2));

    private static bool IsRunId(string id) =>
        id.Length == RunIdHexChars && id.All(character => char.IsAsciiHexDigitLower(character));

    // The typed overload keeps NativeAOT trimming safe: the reflection-based
    // one is flagged by IL2026 and IL3050 at build time.
    private static IResult Problem(int statusCode, string detail) =>
        TypedResults.Json(
            new AnalysisProblem(detail),
            HubJsonContext.Default.AnalysisProblem,
            statusCode: statusCode);
}
