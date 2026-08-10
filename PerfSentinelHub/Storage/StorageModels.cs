namespace PerfSentinelHub.Storage;

public sealed record SourceSnapshot(
    string SourceId,
    string SourceName,
    string Environment,
    string ProducerVersion);

public sealed record StoredFinding(
    string Signature,
    string EnvelopeJson,
    string Service,
    string FindingType,
    string Severity,
    string Endpoint,
    string TemplateHash,
    string? SampleTraceId,
    long FirstSeenMs,
    long LastSeenMs,
    string MaxConfidence,
    int MaxConfidenceRank);

public sealed record FindingSourceObservation(
    string Signature,
    string SourceId,
    string SourceName,
    string Environment,
    string ProducerVersion,
    long FirstSeenMs,
    long LastSeenMs);

public sealed record SourceState(
    string SourceId,
    long LastAttemptMs,
    long? LastSuccessMs,
    long? UnreachableSinceMs,
    string? ProducerVersion,
    string? LastErrorCode);
