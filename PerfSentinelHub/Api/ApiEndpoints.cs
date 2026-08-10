namespace PerfSentinelHub.Api;

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

public static class ApiEndpoints
{
    public static void MapHubApi(this WebApplication app)
    {
        var version = typeof(ApiEndpoints).Assembly.GetName().Version?.ToString() ?? "unknown";

        app.MapGet("/api/status", () => new StatusResponse("perf-sentinel-hub", version));
        app.MapGet("/api/findings", GetFindingsAsync);
        app.MapGet("/api/findings/{traceId}", GetFindingsByTraceAsync);
        app.MapGet("/health/live", () => TypedResults.Ok());
        app.MapGet("/health/ready", (HubDatabase database) =>
            database.IsReady ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
    }

    private static async Task GetFindingsAsync(
        HttpContext context,
        HubDatabase database,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
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
            cancellationToken);
    }

    private static async Task GetFindingsByTraceAsync(
        string traceId,
        HttpResponse response,
        HubDatabase database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var rows = await database.FindByTraceAsync(traceId, cancellationToken);
        await FindingEnvelopeWriter.WriteArrayAsync(
            response,
            rows,
            timeProvider.GetUtcNow(),
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
             limit is < 1 || limit > options.MaxReadLimit))
            return false;

        var includeAcked = true;
        if (request.Query.TryGetValue("include_acked", out var rawIncludeAcked) &&
            !bool.TryParse(rawIncludeAcked[0], out includeAcked))
            return false;

        query = new FindingQuery(
            ReadOptional(request, "service"),
            ReadOptional(request, "finding_type"),
            ReadOptional(request, "severity"),
            limit,
            includeAcked);
        return true;
    }

    private static string? ReadOptional(HttpRequest request, string name) =>
        request.Query.TryGetValue(name, out var value) ? value[0] : null;

    private static bool HasValidUtf8(string? rawQuery)
    {
        if (string.IsNullOrEmpty(rawQuery))
            return true;

        var bytes = new List<byte>(rawQuery.Length);
        for (var index = 0; index < rawQuery.Length; index++)
        {
            if (rawQuery[index] == '%')
            {
                if (index + 2 >= rawQuery.Length ||
                    !byte.TryParse(rawQuery.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                    return false;
                bytes.Add(value);
                index += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(rawQuery[index].ToString()));
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
}
