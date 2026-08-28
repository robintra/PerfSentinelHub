using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    /// <summary>
    /// One daemon's applied settings and its own account of its state. Read on
    /// demand rather than polled: settings never change without a restart the
    /// Hub has no signal for, and the gauges are the whole point of the screen,
    /// so an hour-old copy would be worse than none.
    /// </summary>
    private static async Task GetDaemonViewAsync(
        string sourceId,
        HttpContext context,
        DaemonClient client,
        DaemonViewGate gate,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var source = options.Value.Sources.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, sourceId, StringComparison.Ordinal));
        if (source is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (source.Kind != SourceKinds.Daemon)
        {
            await Problem(
                    StatusCodes.Status400BadRequest,
                    "Only a daemon publishes its settings; this source is a trace backend.")
                .ExecuteAsync(context);
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
            var view = await ReadDaemonAsync(
                client,
                source,
                options.Value.HttpTimeout,
                timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                cancellationToken);
            await DaemonViewWriter.WriteAsync(context.Response, view, cancellationToken);
        }
        finally
        {
            gate.Exit();
        }
    }

    private static async Task<DaemonViewData> ReadDaemonAsync(
        DaemonClient client,
        SourceOptions source,
        TimeSpan timeout,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        // Three independent reads: the latency is the slowest of them rather
        // than their sum, and any one of them can fail without the others.
        var statusTask = TryReadAsync<DaemonStatus>(
            async () => await client.FetchStatusAsync(source, cancellationToken),
            cancellationToken);
        var configTask = TryReadAsync<byte[]>(
            async () => await client.FetchConfigAsync(source, cancellationToken),
            cancellationToken);
        var reportTask = TryReadAsync<byte[]>(
            async () => await client.FetchReportSnapshotAsync(source, timeout, cancellationToken),
            cancellationToken);
        await Task.WhenAll(statusTask, configTask, reportTask);

        var status = statusTask.Result;
        var config = configTask.Result;
        var snapshot = ReadSnapshot(reportTask.Result.Value);

        return new DaemonViewData(
            source.Id,
            observedAtMs,
            DaemonView.Classify(status.Value, snapshot.Warnings.Count),
            status.ErrorCode,
            status.Value,
            ReadConfigObject(config.Value),
            ConfigAbsence(config),
            snapshot.DetectionConfigJson,
            snapshot.ScoringConfigJson,
            snapshot.EnergyModel,
            snapshot.Warnings);
    }

    /// <summary>
    /// Which absence it is. A daemon answering 404 has its query API off, which
    /// is a configuration statement an operator can act on, and not the same
    /// thing as one that did not answer at all.
    /// </summary>
    private static string? ConfigAbsence(DaemonRead<byte[]> config) =>
        config switch
        {
            { ErrorCode: not null } => "unreachable",
            { Value: null } => "api_disabled",
            _ => null
        };

    /// <summary>
    /// Relayed only once it is known to be an object, which bounds the shape
    /// without the Hub having to model any field inside it.
    /// </summary>
    private static string? ReadConfigObject(byte[]? body)
    {
        if (body is null)
            return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? Encoding.UTF8.GetString(body)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The three things a snapshot carries that /api/config does not: the
    /// detection thresholds, the scoring half of the green section, and the
    /// hints the daemon writes about its own tuning.
    /// </summary>
    private static DaemonSnapshotRead ReadSnapshot(byte[]? report)
    {
        if (report is null)
            return new DaemonSnapshotRead(null, null, null, []);

        var warnings = ReportSummary.TryParse(report, out _)?.Warnings ?? [];
        try
        {
            using var document = JsonDocument.Parse(report);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new DaemonSnapshotRead(null, null, null, warnings);

            var green = Section(root, "green_summary");
            return new DaemonSnapshotRead(
                RawObject(root, "detection_config"),
                green is { } summary ? RawObject(summary, "scoring_config") : null,
                green is { } model ? Text(model, "energy_model") : null,
                warnings);
        }
        catch (JsonException)
        {
            return new DaemonSnapshotRead(null, null, null, warnings);
        }
    }

    private static JsonElement? Section(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? RawObject(JsonElement root, string name) =>
        Section(root, name)?.GetRawText();

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task<DaemonRead<T>> TryReadAsync<T>(
        Func<Task<T?>> read,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return new DaemonRead<T>(await read(), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The same vocabulary the poller records, so one source speaks with
            // one set of codes wherever it is read from.
            return new DaemonRead<T>(null, SourcePoller.ErrorCode(exception));
        }
    }

    private sealed record DaemonRead<T>(T? Value, string? ErrorCode) where T : class;

    private sealed record DaemonSnapshotRead(
        string? DetectionConfigJson,
        string? ScoringConfigJson,
        string? EnergyModel,
        IReadOnlyList<ResultWarning> Warnings);
}

/// <summary>
/// Bounds concurrent daemon reads. Each one buffers a report snapshot, so a
/// burst of folds on the Sources screen is real memory rather than a few
/// cheap requests.
/// </summary>
public sealed class DaemonViewGate : IDisposable
{
    public const int MaxReads = 2;

    private readonly SemaphoreSlim _gate = new(MaxReads, MaxReads);

    public bool TryEnter() => _gate.Wait(0);

    public void Exit() => _gate.Release();

    public void Dispose() => _gate.Dispose();
}
