using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

/// <summary>
/// The Prometheus exposition format, written by hand. The whole surface is six
/// metric families over data the Hub already holds, which is not worth a
/// dependency in a NativeAOT service whose only two packages are SQLite.
///
/// Cardinality is bounded by configuration, not by requests: `source` takes the
/// ids in `Hub:Sources`, fixed at startup and validated there, and `status`
/// takes the six constants in <see cref="AnalysisStatuses"/>. Nothing a caller
/// sends reaches a label.
/// </summary>
public static class MetricsEndpoint
{
    private const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    public static void MapMetrics(this WebApplication app)
    {
        var version = typeof(MetricsEndpoint).Assembly.GetName().Version?.ToString() ?? "unknown";
        app.MapGet("/metrics", async (
            HubDatabase database,
            IOptions<HubOptions> options,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var body = await RenderAsync(database, options.Value, timeProvider, version, cancellationToken);
            return Results.Text(body, ContentType);
        });
    }

    private static async Task<string> RenderAsync(
        HubDatabase database,
        HubOptions options,
        TimeProvider timeProvider,
        string version,
        CancellationToken cancellationToken)
    {
        var states = await database.QuerySourceStatesAsync(cancellationToken);
        var runs = await database.CountRunsByStatusAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        // Read from the same snapshot as the per-status series rather than from
        // a second query: two counts of the same rows taken a moment apart can
        // disagree inside one scrape.
        var queued = Runs(runs, AnalysisStatuses.Pending);

        var text = new StringBuilder();
        Family(text, "perf_sentinel_hub_build_info", "gauge", "The running version, always 1.");
        // Escaped like any label value, though an assembly version cannot carry a
        // reserved character.
        Line(text, "perf_sentinel_hub_build_info", $"version=\"{Escape(version)}\"", 1);

        // Only a daemon is polled, so reachability is meaningless for a trace
        // backend and no series is written for one.
        //
        // The three families below are three loops on purpose. Every sample of a
        // family has to be contiguous, so merging them into one pass over the
        // sources would interleave families and produce a scrape Prometheus
        // rejects.
        var daemons = options.Sources.Where(source => source.Kind == SourceKinds.Daemon).ToList();

        Family(text, "perf_sentinel_hub_source_reachable", "gauge",
            "1 when the Hub's last poll of this daemon succeeded, 0 while it is unreachable.");
        foreach (var source in daemons)
        {
            states.TryGetValue(source.Id, out var state);
            Line(text, "perf_sentinel_hub_source_reachable", Label(source.Id),
                state?.UnreachableSinceMs is null ? 1 : 0);
        }

        Family(text, "perf_sentinel_hub_source_unreachable_seconds", "gauge",
            "How long this daemon has been unreachable, 0 while it answers.");
        foreach (var source in daemons)
        {
            states.TryGetValue(source.Id, out var state);
            Line(text, "perf_sentinel_hub_source_unreachable_seconds", Label(source.Id),
                state?.UnreachableSinceMs is { } since ? Seconds(now - since) : 0);
        }

        // A source never observed gets no series rather than a zero: zero would
        // read as "succeeded just now", which is the opposite of the truth.
        Family(text, "perf_sentinel_hub_source_last_success_seconds", "gauge",
            "Age of the last successful poll. Absent for a daemon never polled successfully.");
        foreach (var source in daemons)
        {
            states.TryGetValue(source.Id, out var state);
            if (state?.LastSuccessMs is { } success)
            {
                Line(text, "perf_sentinel_hub_source_last_success_seconds", Label(source.Id),
                    Seconds(now - success));
            }
        }

        Family(text, "perf_sentinel_hub_analysis_queue_depth", "gauge",
            "Analysis runs accepted and not yet claimed by a worker.");
        Line(text, "perf_sentinel_hub_analysis_queue_depth", null, queued);

        // A gauge, not a counter: retention deletes rows, so these fall as well
        // as rise and naming them _total would promise a monotonic series.
        Family(text, "perf_sentinel_hub_analysis_runs", "gauge",
            "Analysis runs currently stored, by status. Retention removes them, so this is not a total.");
        foreach (var status in AnalysisStatuses.All)
        {
            Line(text, "perf_sentinel_hub_analysis_runs", $"status=\"{status}\"",
                Runs(runs, status));
        }

        return text.ToString();
    }

    private static void Family(StringBuilder text, string name, string type, string help)
    {
        text.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        text.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
    }

    private static void Line(StringBuilder text, string name, string? labels, double value)
    {
        text.Append(name);
        if (labels is not null)
        {
            text.Append('{').Append(labels).Append('}');
        }

        text.Append(' ').Append(value.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
    }

    private static string Label(string sourceId) => $"source=\"{Escape(sourceId)}\"";

    /// <summary>
    /// A stored timestamp ahead of the Hub's clock, from skew or from a restored
    /// backup, clamps to zero. Zero here reads as "just now", which understates
    /// an age rather than overstating it, so a duration alert can miss. The
    /// alternative, publishing a negative age, breaks every threshold instead.
    /// </summary>
    private static double Seconds(long milliseconds) => Math.Max(0, milliseconds) / 1000.0;

    private static int Runs(IReadOnlyDictionary<string, int> runs, string status) =>
        runs.TryGetValue(status, out var count) ? count : 0;

    /// <summary>
    /// Backslash, quote and newline, the three the exposition format reserves in
    /// a label value. None can reach here today: `HubOptions.IsValidSourceId`
    /// allows only ASCII alphanumerics, '.', '_' and '-'. Kept so that loosening
    /// that rule cannot silently produce a malformed scrape.
    /// </summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal)
             .Replace("\n", "\\n", StringComparison.Ordinal);
}
