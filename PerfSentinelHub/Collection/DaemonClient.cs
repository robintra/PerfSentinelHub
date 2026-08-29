using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Collection;

/// <summary>
/// What a daemon says about itself right now. Version stays mandatory, it is
/// what the poller records as the producer version, so a body without one is
/// still an InvalidStatusException. The gauges are optional: a capacity is null
/// when the field is missing or zero, because a capacity of zero is not a
/// capacity but an unknown.
/// </summary>
public sealed record DaemonStatus(
    string Version,
    long? UptimeSeconds,
    long? ActiveTraces,
    long? MaxActiveTraces,
    long? AnalysisQueueDepth,
    long? AnalysisQueueCapacity,
    long? StoredFindings,
    long? MaxRetainedFindings);

public sealed class DaemonClient(HttpClient httpClient, IOptions<HubOptions> options)
{
    private const int MaxBodyBytes = 16 * 1024 * 1024;
    private const int ConfigMaxBytes = 64 * 1024;
    internal const int FindingsLimit = 1000;
    private readonly TimeSpan _timeout = options.Value.HttpTimeout;

    public async Task<DaemonStatus> FetchStatusAsync(
        SourceOptions source,
        CancellationToken cancellationToken)
    {
        var body = await SendAsync(source, "api/status", cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("version", out var version) &&
                version.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(version.GetString()))
                return ReadStatus(root, version.GetString()!);
        }
        catch (JsonException exception)
        {
            throw new InvalidStatusException(exception);
        }

        throw new InvalidStatusException();
    }

    /// <summary>
    /// The effective [daemon] section. Null when the daemon answers 404: the
    /// endpoint only exists with `[daemon] api_enabled = true`, and both cases
    /// are "nothing to show" rather than a failure.
    /// </summary>
    public async Task<byte[]?> FetchConfigAsync(
        SourceOptions source,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendAsync(source, "api/config", cancellationToken, maxBytes: ConfigMaxBytes);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static DaemonStatus ReadStatus(JsonElement root, string version) => new(
        version,
        ReadGauge(root, "uptime_seconds"),
        ReadGauge(root, "active_traces"),
        ReadCapacity(root, "max_active_traces"),
        ReadDepth(root, "analysis_queue_depth"),
        ReadCapacity(root, "analysis_queue_capacity"),
        ReadGauge(root, "stored_findings"),
        ReadCapacity(root, "max_retained_findings"));

    private static long? ReadGauge(JsonElement root, string name) =>
        Api.JsonRead.ReadLong(root, name);

    // Normalised here rather than at every reader: a capacity of zero and an
    // absent one are the same question, and only one of them has to be asked.
    private static long? ReadCapacity(JsonElement root, string name) =>
        ReadGauge(root, name) is { } capacity and > 0 ? capacity : null;

    // The queue depth travels as a signed Prometheus gauge and can dip below
    // zero between a pop and its decrement.
    private static long? ReadDepth(JsonElement root, string name) =>
        ReadGauge(root, name) is { } depth ? Math.Max(0, depth) : null;

    /// <summary>
    /// The daemon's own rendered report, whatever it holds in memory right
    /// now. Carries its own timeout: an export is heavier than a status read,
    /// and reporting a slow daemon as unreachable would name the wrong owner.
    /// </summary>
    public Task<byte[]> FetchReportSnapshotAsync(
        SourceOptions source,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        SendAsync(source, "api/export/report", cancellationToken, timeout);

    public Task<byte[]> FetchFindingsAsync(
        SourceOptions source,
        CancellationToken cancellationToken) =>
        SendAsync(source, $"api/findings?limit={FindingsLimit}&include_acked=true", cancellationToken);

    private async Task<byte[]> SendAsync(
        SourceOptions source,
        string path,
        CancellationToken cancellationToken,
        TimeSpan? overrideTimeout = null,
        int maxBytes = MaxBodyBytes)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(overrideTimeout ?? _timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, RequestUri(source, path));
        if (source.AuthHeaderName is not null)
            request.Headers.TryAddWithoutValidation(source.AuthHeaderName, source.AuthHeaderValue);

        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maxBytes)
                throw new ResponseTooLargeException();

            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = await input.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                    break;
                if (output.Length + read > maxBytes)
                    throw new ResponseTooLargeException();
                await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }
            return output.ToArray();
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DaemonTimeoutException(exception);
        }
    }

    // The path stays relative and the base keeps its trailing slash: a rooted path would discard
    // any path prefix configured on Sources[].BaseUrl (a daemon behind a path-based ingress).
    private static Uri RequestUri(SourceOptions source, string path)
    {
        var baseUrl = source.BaseUrl!;
        return new Uri(
            baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri($"{baseUrl.AbsoluteUri}/"),
            path);
    }
}

public sealed class DaemonTimeoutException(Exception innerException)
    : IOException("The daemon request timed out.", innerException);

public sealed class ResponseTooLargeException()
    : IOException("The daemon response exceeds 16 MiB.");

public sealed class InvalidStatusException : IOException
{
    public InvalidStatusException() : base("The daemon status response is invalid.") { }
    public InvalidStatusException(Exception innerException)
        : base("The daemon status response is invalid.", innerException) { }
}
