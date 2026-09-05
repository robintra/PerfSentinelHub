namespace PerfSentinelHub.Api;

public sealed record StatusResponse(
    string Service,
    string Version,
    // Who the reverse proxy says is calling. Null when no proxy set the header,
    // which is the whole truth the Hub has: it never verifies the claim.
    string? Identity,
    string? EngineVersion,
    // The newest published release of each product, or null when the check is
    // off, has not run yet, could not reach GitHub, or found no release at all.
    // The Hub states them and lets the page decide what they mean.
    string? LatestEngineVersion,
    string? LatestHubVersion,
    int QueueDepth,
    int Workers,
    StatusLimits Limits,
    // The detection thresholds a run may override, with the engine's own
    // defaults and the bounds a submission is held to.
    IReadOnlyList<DetectionKnob> DetectionKnobs);

/// <summary>
///     What a run costs, reported rather than assumed: the launcher shows these
///     four figures next to the button instead of guessing them. The read
///     limit is the operator's ceiling on a listing page, so the launcher sizes
///     its pages under it instead of learning it from a 400.
/// </summary>
public sealed record StatusLimits(
    int MaxTracesCap,
    int AnalysisTimeoutSeconds,
    int ReportRetentionHours,
    int MaxTracesEmbedded,
    int MaxReadLimit);

/// <summary>
///     One detection threshold a run may override. `Kind` is `integer`, `decimal`
///     or `choice`: the first two carry bounds and a numeric default, the last
///     carries its choices and a string default. `Default` is the engine's own
///     value as JSON, relayed rather than typed.
/// </summary>
public sealed record DetectionKnob(
    string Name,
    string Kind,
    double? Min,
    double? Max,
    System.Text.Json.JsonElement Default,
    IReadOnlyList<string>? Choices);

/// <summary>
///     A configured source joined to its last known collection state. The
///     timestamps are null for a source that has never been observed, so the
///     launcher can say "never polled" instead of showing the epoch.
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
    string? LastErrorCode,
    // The endpoint a command would target, in the exact form the Hub passes to
    // the engine itself. Published rather than rebuilt in the page: the two
    // spellings have to be the same bytes.
    string BaseUrl,
    // The engine subcommand for this kind, null for a daemon: a daemon is not
    // queried, it is read.
    string? EngineSubcommand,
    // The header's name, never its value. Without it the note cannot say what
    // belongs in the variable, which the engine expects as "Name: Value".
    string? AuthHeaderName,
    // What the last incidents read of this daemon came to: ok, absent (no such
    // route, or the store is off), unauthorized, error. Null when none has run.
    string? IncidentsState);

public sealed record ImportResponse(int Accepted, int Rejected);

public sealed record FindingQuery(
    string? Service,
    string? FindingType,
    string? Severity,
    int Limit,
    bool IncludeAcked = true,
    string? Status = null);

public sealed record IncidentQuery(string? Service, string? SourceId, int Offset, int Limit);

public sealed record SubmittedAnalysis(string Id, string Status);

public sealed record AnalysisProblem(string Detail);
