using System.Diagnostics.CodeAnalysis;

namespace PerfSentinelHub.Storage;

public sealed record SourceSnapshot(
    string SourceId,
    string SourceName,
    string Environment,
    string ProducerVersion);

public sealed record StoredFinding(
    [property: SuppressMessage(
        "ReSharper",
        "NotAccessedPositionalProperty.Global",
        Justification = "Queried by storage contract tests; the HTTP writer preserves the original envelope.")]
    string Signature,
    string EnvelopeJson,
    long FirstSeenMs,
    long LastSeenMs,
    string MaxConfidence,
    string Status,
    List<FindingSourceObservation> Sources)
{
    /// <summary>
    ///     Chain back to the finding this one mutated from, attached after the
    ///     main query when a lineage link exists. Null for a finding with no
    ///     recorded predecessor.
    /// </summary>
    public LineageInfo? Lineage { get; set; }
}

/// <summary>
///     Flattened view of a finding's mutation chain: the earliest predecessor
///     birth (surviving the predecessors' retention purge, since it is copied
///     at link time) and how many hops the chain holds.
/// </summary>
public sealed record LineageInfo(long OriginalFirstSeenMs, int Predecessors);

public sealed record FindingSourceObservation(
    string SourceId,
    string SourceName,
    string Environment,
    string ProducerVersion,
    long LastSeenMs,
    long? UnreachableSinceMs);

/// <summary>
///     Last known collection state for one configured source. Every field is
///     nullable because a source that has never been polled, and a push-only
///     source before its first import, both have no row at all.
/// </summary>
public sealed record SourceState(
    long? LastAttemptMs,
    long? LastSuccessMs,
    long? UnreachableSinceMs,
    string? ProducerVersion,
    string? LastErrorCode);

/// <summary>
///     One daemon incident as the Hub keeps it: the daemon's document whole,
///     the columns the listing filters and orders by, and the Hub's own clock
///     for when the copy was first and last refreshed.
/// </summary>
public sealed record StoredIncident(
    string Id,
    string SourceId,
    string Service,
    string Kind,
    long AtMs,
    long? EndedAtMs,
    long WindowFromMs,
    long WindowToMs,
    long? OldestFindingMs,
    int FindingCount,
    string IncidentJson,
    long FirstSeenMs,
    long LastSeenMs);

/// <summary>The outcome of the last incidents read of one source.</summary>
public sealed record IncidentRead(long LastReadMs, string State, string? LastErrorCode);

public static class IncidentReadStates
{
    public const string Ok = "ok";
    public const string Absent = "absent";
    public const string Unauthorized = "unauthorized";
    public const string Error = "error";
}

public static class AnalysisStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Interrupted = "interrupted";
    public const string Expired = "expired";

    /// <summary>
    ///     Every status, so a reader can emit a series for each one. A gauge that
    ///     vanishes when it reaches zero reads as a scrape failure rather than as
    ///     "nothing is in that state".
    /// </summary>
    public static readonly string[] All =
        [Pending, Running, Succeeded, Failed, Interrupted, Expired];
}

/// <summary>
///     One analysis run. The source's name, environment and kind are copied at
///     submission because a run outlives the configuration entry it came from.
///     `RequestJson` and `ResultJson` stay opaque here: their shape is the
///     launcher's contract and varies with the source kind.
/// </summary>
public sealed record AnalysisRun(
    string Id,
    string Status,
    string SourceId,
    string SourceName,
    string Environment,
    string Kind,
    string RequestJson,
    string RequestedBy,
    long CreatedAtMs,
    long? StartedAtMs,
    long? FinishedAtMs,
    long? ExpiresAtMs,
    string? ProducerVersion,
    string? ErrorCode,
    string? ResultJson);
