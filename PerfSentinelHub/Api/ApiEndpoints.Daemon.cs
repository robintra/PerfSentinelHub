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
        EngineProbe engine,
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
                // Null when this Hub has no binary configured. The defaults are
                // still the ones it was built with, so they are still published
                // and the reader is told they belong to nothing it can name.
                engine.Version ?? "unknown",
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
        string defaultsEngineVersion,
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
            // Three times the ordinary budget: an export is heavier than a
            // status read, which is why FetchReportSnapshotAsync takes its
            // own timeout in the first place.
            async () => await client.FetchReportSnapshotAsync(source, timeout * 3, cancellationToken),
            cancellationToken);
        await Task.WhenAll(statusTask, configTask, reportTask);

        var status = statusTask.Result;
        var config = configTask.Result;
        var report = reportTask.Result;
        var snapshot = ReadSnapshot(report.Value);
        var relayedConfig = ReadConfigObject(config.Value);

        return new DaemonViewData(
            source.Id,
            observedAtMs,
            // An unread export means the daemon's own hints are unknown, and
            // "ok" is a claim about those hints as much as about the gauges.
            DaemonView.Classify(status.Value, snapshot.Warnings.Count, report.ErrorCode is null),
            status.ErrorCode,
            status.Value,
            relayedConfig,
            ConfigAbsence(config, relayedConfig),
            snapshot.DetectionConfigJson,
            snapshot.ScoringConfigJson,
            snapshot.EnergyModel,
            defaultsEngineVersion,
            report.ErrorCode,
            snapshot.Warnings,
            snapshot.Dropped);
    }

    /// <summary>
    /// Which absence it is, three different actions for an operator: a 404 is
    /// the daemon saying its query API is off, a network failure is nothing
    /// answering, and everything else answered with something the Hub refused
    /// to relay, an error status, an oversized section, or a body that is not
    /// the [daemon] object.
    /// </summary>
    private static string? ConfigAbsence(DaemonRead<byte[]> config, string? relayed) =>
        config switch
        {
            { ErrorCode: "network_error" or "timeout" } => "unreachable",
            { ErrorCode: not null } => "unreadable",
            { Value: null } => "api_disabled",
            _ => relayed is null ? "unreadable" : null
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

    private const int MaxHints = 100;
    private const int MaxHintChars = 2000;

    /// <summary>
    /// The three things a snapshot carries that /api/config does not: the
    /// detection thresholds, the scoring half of the green section, and the
    /// hints the daemon writes about its own tuning. One parse of the body
    /// serves all of them.
    /// </summary>
    private static DaemonSnapshotRead ReadSnapshot(byte[]? report)
    {
        if (report is null)
            return new DaemonSnapshotRead(null, null, null, [], 0);

        try
        {
            using var document = JsonDocument.Parse(report);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new DaemonSnapshotRead(null, null, null, [], 0);

            var (warnings, dropped) = ReadHints(root);
            var green = Section(root, "green_summary");
            return new DaemonSnapshotRead(
                RawObject(root, "detection_config"),
                green is { } summary ? RawObject(summary, "scoring_config") : null,
                green is { } model ? Text(model, "energy_model") : null,
                warnings,
                dropped);
        }
        catch (JsonException)
        {
            return new DaemonSnapshotRead(null, null, null, [], 0);
        }
    }

    /// <summary>
    /// The daemon's hints, wide enough that truncation is theoretical (the
    /// daemon has ten advisor rules), and never silent when it happens: a cut
    /// message carries a visible ellipsis and the dropped count goes on the
    /// wire. ReportSummary's tighter run-storage bounds stay its own.
    /// </summary>
    private static (IReadOnlyList<ResultWarning> Warnings, int Dropped) ReadHints(JsonElement root)
    {
        var warnings = new List<ResultWarning>();
        var dropped = 0;
        if (root.TryGetProperty("warning_details", out var details) &&
            details.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in details.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;
                if (Text(entry, "kind") is not { } kind || Text(entry, "message") is not { } message)
                    continue;
                if (warnings.Count == MaxHints) { dropped++; continue; }
                warnings.Add(new ResultWarning(kind, TruncateHint(message)));
            }
        }

        if (warnings.Count > 0 || dropped > 0 ||
            !root.TryGetProperty("warnings", out var legacy) ||
            legacy.ValueKind != JsonValueKind.Array)
            return (warnings, dropped);

        foreach (var entry in legacy.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
                continue;
            if (warnings.Count == MaxHints) { dropped++; continue; }
            warnings.Add(new ResultWarning("unknown", TruncateHint(entry.GetString()!)));
        }

        return (warnings, dropped);
    }

    private static string TruncateHint(string message) =>
        message.Length <= MaxHintChars ? message : message[..MaxHintChars] + "…";

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
        IReadOnlyList<ResultWarning> Warnings,
        int Dropped);
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
