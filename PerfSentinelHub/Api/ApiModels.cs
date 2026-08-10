namespace PerfSentinelHub.Api;

public sealed record StatusResponse(string Service, string Version);

public sealed record FindingQuery(
    string? Service,
    string? FindingType,
    string? Severity,
    int Limit,
    bool IncludeAcked = true);
