using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

/// <summary>
///     One sample of perf_sentinel_hub_findings. The environment is configured,
///     the type and the severity are already folded into their closed vocabularies.
/// </summary>
public sealed record FindingSeries(string Environment, FindingCount Findings);

/// <summary>
///     The findings family of /metrics, counted at most once per scrape interval.
///     The route is anonymous and ungated and a scoped count reads its whole
///     scope, so an uncached one would let any caller make the Hub count at will.
///     Every other family is cheap and stays computed at scrape time.
/// </summary>
public sealed class FindingMetrics(
    HubDatabase database,
    IOptions<HubOptions> options,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);

    // Written and read on request threads. Volatile on both sides for the reason
    // UpdateChecker gives: a plain reference store carries no barrier.
    // ponytail: scrapes that find it stale at the same moment each count once,
    // a SemaphoreSlim around the count if a burst of them ever shows up.
    private Snapshot? _snapshot;

    /// <summary>
    ///     One sample per environment, type, severity and status that holds a
    ///     finding. An environment is a group of `Hub:Sources`, never a stored
    ///     row: that column goes stale when a source changes environment.
    /// </summary>
    public async Task<IReadOnlyList<FindingSeries>> SeriesAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _snapshot) is { } snapshot &&
            timeProvider.GetElapsedTime(snapshot.CountedAt) < Lifetime)
            return snapshot.Series;

        var countedAt = timeProvider.GetTimestamp();
        var series = await CountAsync(cancellationToken);
        Volatile.Write(ref _snapshot, new Snapshot(countedAt, series));
        return series;
    }

    private async Task<IReadOnlyList<FindingSeries>> CountAsync(CancellationToken cancellationToken)
    {
        var series = new List<FindingSeries>();
        var environments = options.Value.Sources.GroupBy(source => source.Environment, StringComparer.Ordinal);
        foreach (var environment in environments)
        {
            var counts = await database.CountFindingsAsync(
                [.. environment.Select(source => source.Id)], cancellationToken);
            // Two stored values can fold into one label, and a scrape that repeats
            // a series is rejected whole, so folded rows are summed.
            series.AddRange(counts
                .GroupBy(count => (
                    Type: FindingLabels.Fold(FindingLabels.Types, count.FindingType),
                    Severity: FindingLabels.Fold(FindingLabels.Severities, count.Severity),
                    count.Status))
                .Select(group => new FindingSeries(
                    environment.Key,
                    new FindingCount(
                        group.Key.Type, group.Key.Severity, group.Key.Status, group.Sum(count => count.Count)))));
        }

        return series;
    }

    private sealed record Snapshot(long CountedAt, IReadOnlyList<FindingSeries> Series);
}
