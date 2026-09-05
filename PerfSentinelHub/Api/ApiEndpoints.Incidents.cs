using System.Globalization;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
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
            !TryReadBounded(request, "offset", 0, 0, int.MaxValue, out var offset))
            return false;

        // An unknown source id is a bad request rather than an empty page: the
        // ids are configuration, and a typo would otherwise read as "no incidents".
        var sourceId = ReadOptional(request, "source_id");
        if (sourceId is not null &&
            !options.Sources.Any(source => string.Equals(source.Id, sourceId, StringComparison.Ordinal)))
            return false;

        query = new IncidentQuery(ReadOptional(request, "service"), sourceId, offset, limit);
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
