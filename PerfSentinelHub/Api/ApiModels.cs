namespace PerfSentinelHub.Api;

public sealed record StatusResponse(
    string Service,
    string Version,
    // Who the reverse proxy says is calling. Null when no proxy set the header,
    // which is the whole truth the Hub has: it never verifies the claim.
    string? Identity,
    string? EngineVersion,
    int QueueDepth,
    int Workers,
    StatusLimits Limits,
    // The detection thresholds a run may override, with the engine's own
    // defaults and the bounds a submission is held to.
    IReadOnlyList<DetectionKnob> DetectionKnobs);

/// <summary>
/// What a run costs, reported rather than assumed: the launcher shows these
/// four figures next to the button instead of guessing them.
/// </summary>
public sealed record StatusLimits(
    int MaxTracesCap,
    int AnalysisTimeoutSeconds,
    int ReportRetentionHours);

public sealed record DetectionKnob(string Name, int Min, int Max, int Default);

/// <summary>
/// A configured source joined to its last known collection state. The
/// timestamps are null for a source that has never been observed, so the
/// launcher can say "never polled" instead of showing the epoch.
/// </summary>
public sealed record SourceResponse(
    string Id,
    string Name,
    string Environment,
    string Kind,
    // Declared in the Hub's configuration, null when nobody did.
    int? RetentionHours,
    bool Reachable,
    long? LastAttemptMs,
    long? LastSuccessMs,
    long? UnreachableSinceMs,
    string? ProducerVersion,
    string? LastErrorCode);

public sealed record ImportResponse(int Accepted, int Rejected);

public sealed record FindingQuery(
    string? Service,
    string? FindingType,
    string? Severity,
    int Limit,
    bool IncludeAcked = true,
    string? Status = null);

public sealed record SubmittedAnalysis(string Id, string Status);

public sealed record AnalysisProblem(string Detail);
