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
    /// Chain back to the finding this one mutated from, attached after the
    /// main query when a lineage link exists. Null for a finding with no
    /// recorded predecessor.
    /// </summary>
    public LineageInfo? Lineage { get; set; }
}

/// <summary>
/// Flattened view of a finding's mutation chain: the earliest predecessor
/// birth (surviving the predecessors' retention purge, since it is copied
/// at link time) and how many hops the chain holds.
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
/// Last known collection state for one configured source. Every field is
/// nullable because a source that has never been polled, and a push-only
/// source before its first import, both have no row at all.
/// </summary>
public sealed record SourceState(
    long? LastAttemptMs,
    long? LastSuccessMs,
    long? UnreachableSinceMs,
    string? ProducerVersion,
    string? LastErrorCode);
