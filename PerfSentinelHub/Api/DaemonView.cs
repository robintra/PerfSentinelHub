using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Api;

/// <summary>
///     How a daemon reads at a glance. The daemon writes its own recommendations,
///     from counters the Hub never sees, so nothing here produces one: the only
///     thing derived is whether a gauge crossed the line the daemon's own advisor
///     uses, which it publishes a value and a cap for but never a ratio.
/// </summary>
public static class DaemonView
{
    // Mirrors ADVISOR_THRESHOLD_PCT in the engine's query monitor, itself the
    // ratio behind the daemon's "window nearly full" hint. The Hub colours the
    // same line rather than drawing one of its own.
    private const double AdvisorThresholdPct = 90.0;

    public const string Unreachable = "unreachable";
    public const string NearCapacity = "near_capacity";
    public const string Advised = "advised";
    public const string Unknown = "unknown";
    public const string Ok = "ok";

    public static DaemonGauges Read(DaemonStatus status)
    {
        return new DaemonGauges(
            Gauge(status.ActiveTraces, status.MaxActiveTraces),
            Gauge(status.AnalysisQueueDepth, status.AnalysisQueueCapacity),
            Gauge(status.StoredFindings, status.MaxRetainedFindings));
    }

    public static string Classify(DaemonStatus? status, int warningCount, bool hintsKnown)
    {
        if (status is null)
            return Unreachable;

        var gauges = Read(status);
        if (gauges.Traces.AtCapacity || gauges.AnalysisQueue.AtCapacity || gauges.Findings.AtCapacity)
            return NearCapacity;
        if (warningCount > 0)
            return Advised;
        // "ok" is a claim about the daemon's own hints as much as about the
        // gauges: with the export unread, silence proves nothing.
        if (!hintsKnown)
            return Unknown;
        // Nothing measurable and nothing reported is not a clean bill of
        // health: a daemon that publishes no capacity leaves the Hub without
        // evidence, and "ok" would be a claim it cannot make.
        return gauges.Traces.Pct is null && gauges.AnalysisQueue.Pct is null && gauges.Findings.Pct is null
            ? Unknown
            : Ok;
    }

    private static DaemonGauge Gauge(long? value, long? capacity)
    {
        // A cap of zero is guarded here as well as at the parser: dividing by it
        // would clamp to a full gauge and report a healthy daemon as saturated.
        if (value is not { } gauge || capacity is not { } cap || cap <= 0)
            return new DaemonGauge(value, capacity, null, false);

        var pct = Math.Clamp((double)gauge / cap * 100.0, 0.0, 100.0);
        return new DaemonGauge(gauge, cap, pct, pct >= AdvisorThresholdPct);
    }
}

/// <summary>
///     One figure against the cap it runs into. Pct is null when either side is
///     unknown, which keeps the ratio out of the state rules instead of letting a
///     missing cap read as room to spare.
/// </summary>
public sealed record DaemonGauge(long? Value, long? Capacity, double? Pct, bool AtCapacity);

public sealed record DaemonGauges(DaemonGauge Traces, DaemonGauge AnalysisQueue, DaemonGauge Findings);
