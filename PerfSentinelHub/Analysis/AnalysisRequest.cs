using System.Globalization;
using System.Text.Json;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Analysis;

/// <summary>
/// What the operator asked for, in the shape the launcher submits it. The
/// engine's own exclusions are enforced here rather than discovered as a
/// non-zero exit: a trace ID resolves to one trace so no window applies to it,
/// and a relative window conflicts with an absolute one.
/// </summary>
public sealed record AnalysisRequest
{
    public string? Service { get; init; }
    public string? TraceId { get; init; }
    public string? Lookback { get; init; }
    public long? FromMs { get; init; }
    public long? ToMs { get; init; }
    public int? MaxTraces { get; init; }

    private const int MaxServiceLength = 256;
    private const int MaxTraceIdLength = 64;
    private const int DefaultMaxTraces = 100;

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>
    /// Parses and validates a submitted request against the source it targets.
    /// Returns null and an operator-facing reason when the pair cannot run.
    /// </summary>
    public static AnalysisRequest? TryParse(
        JsonElement payload,
        SourceOptions source,
        AnalysisOptions analysis,
        long nowMs,
        out string? error)
    {
        error = null;
        // An omitted request is the empty object, which is exactly what a
        // daemon takes. Anything present but not an object is still refused.
        if (payload.ValueKind == JsonValueKind.Undefined || payload.ValueKind == JsonValueKind.Null)
            payload = EmptyObject;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "The request must be a JSON object.";
            return null;
        }

        var request = new AnalysisRequest
        {
            Service = ReadString(payload, "service"),
            TraceId = ReadString(payload, "trace_id"),
            Lookback = ReadString(payload, "lookback"),
            FromMs = ReadLong(payload, "from_ms"),
            ToMs = ReadLong(payload, "to_ms"),
            MaxTraces = ReadInt(payload, "max_traces")
        };

        error = source.Kind == SourceKinds.Daemon
            ? ValidateDaemon(request)
            : ValidateBackend(request, analysis, nowMs);
        return error is null ? request : null;
    }

    /// <summary>
    /// The engine arguments this request becomes, for the subcommand matching
    /// the source kind. Never called for a daemon: a snapshot is an HTTP read.
    /// </summary>
    public IReadOnlyList<string> ToEngineArguments(SourceOptions source)
    {
        var arguments = new List<string>
        {
            source.Kind == SourceKinds.Tempo ? "tempo" : "jaeger-query",
            "--endpoint",
            source.BaseUrl!.ToString().TrimEnd('/'),
            "--format",
            "json"
        };

        if (TraceId is { } traceId)
        {
            arguments.Add("--trace-id");
            arguments.Add(traceId);
            return arguments;
        }

        arguments.Add("--service");
        arguments.Add(Service!);
        if (FromMs is { } fromMs && ToMs is { } toMs)
        {
            arguments.Add("--from");
            arguments.Add(ToIso8601(fromMs));
            arguments.Add("--to");
            arguments.Add(ToIso8601(toMs));
        }
        else
        {
            arguments.Add("--lookback");
            arguments.Add(Lookback!);
        }

        arguments.Add("--max-traces");
        arguments.Add((MaxTraces ?? DefaultMaxTraces).ToString(CultureInfo.InvariantCulture));
        return arguments;
    }

    private static string? ValidateDaemon(AnalysisRequest request) =>
        request.Service is null &&
        request.TraceId is null &&
        request.Lookback is null &&
        request.FromMs is null &&
        request.ToMs is null &&
        request.MaxTraces is null
            ? null
            // A daemon snapshot is whatever it holds in memory right now.
            // Asking for a window would be a request the source cannot answer.
            : "A daemon snapshot takes no parameters.";

    private static string? ValidateBackend(AnalysisRequest request, AnalysisOptions analysis, long nowMs)
    {
        if (request.TraceId is { } traceId)
            return ValidateTraceRequest(request, traceId);
        if (request.Service is not { } service)
            return "Either a service or a trace ID is required.";
        if (service.Length is 0 or > MaxServiceLength)
            return $"The service name must be 1 to {MaxServiceLength} characters.";

        return ValidateWindow(request, nowMs) ?? ValidateMaxTraces(request, analysis);
    }

    private static string? ValidateTraceRequest(AnalysisRequest request, string traceId)
    {
        if (traceId.Length is 0 or > MaxTraceIdLength ||
            !traceId.All(char.IsAsciiLetterOrDigit))
            return $"A trace ID must be 1 to {MaxTraceIdLength} letters or digits.";
        // The engine refuses the combination outright, and the launcher hides
        // the whole window block in trace mode.
        return request.Service is null &&
               request.Lookback is null &&
               request.FromMs is null &&
               request.ToMs is null
            ? null
            : "A trace ID resolves to exactly one trace, so no window applies to it.";
    }

    private static string? ValidateWindow(AnalysisRequest request, long nowMs)
    {
        var hasAbsolute = request.FromMs is not null || request.ToMs is not null;
        if (request.Lookback is not null && hasAbsolute)
            return "A relative window and an absolute window are mutually exclusive.";
        if (!hasAbsolute)
        {
            // No implicit window. The engine would apply its own default of 1h
            // and the stored run would not say which window it read, leaving a
            // card nobody can interpret afterwards.
            if (request.Lookback is null)
                return "A service request needs either a lookback or an absolute window.";
            return IsLookback(request.Lookback)
                ? null
                : "A lookback is a number followed by d, h, m or s, for example 90m.";
        }
        if (request.FromMs is not { } fromMs || request.ToMs is not { } toMs)
            return "An absolute window needs both from_ms and to_ms.";
        if (fromMs >= toMs)
            return "The window's start must come before its end.";
        return toMs > nowMs ? "The window's end cannot be in the future." : null;
    }

    private static string? ValidateMaxTraces(AnalysisRequest request, AnalysisOptions analysis) =>
        request.MaxTraces is not { } maxTraces || (maxTraces >= 1 && maxTraces <= analysis.MaxTracesCap)
            ? null
            : $"max_traces must be between 1 and {analysis.MaxTracesCap}.";

    private static bool IsLookback(string value) =>
        value.Length is >= 2 and <= 8 &&
        value[^1] is 'd' or 'h' or 'm' or 's' &&
        value[..^1].All(char.IsAsciiDigit) &&
        value[..^1].TrimStart('0').Length > 0;

    private static string ToIso8601(long epochMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(epochMs)
            .UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string? ReadString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // TryGetInt64 throws on a non-number element rather than returning false,
    // so the kind check is load-bearing, not defensive.
    private static long? ReadLong(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static int? ReadInt(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}
