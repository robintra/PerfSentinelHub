namespace PerfSentinelHub.Api;

public sealed record StatusResponse(
    string Service,
    string Version,
    string? EngineVersion,
    int QueueDepth,
    int Workers,
    StatusLimits Limits);

/// <summary>
/// What a run costs, reported rather than assumed: the launcher shows these
/// four figures next to the button instead of guessing them.
/// </summary>
public sealed record StatusLimits(
    int MaxTracesCap,
    int AnalysisTimeoutSeconds,
    int ReportRetentionHours);

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
