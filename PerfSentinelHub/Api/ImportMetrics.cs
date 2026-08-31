namespace PerfSentinelHub.Api;

/// <summary>
///     Why an import was refused. A closed set: these become Prometheus label
///     values, and a label fed from anything a caller controls is how a /metrics
///     endpoint dies of cardinality.
/// </summary>
public enum ImportRejection
{
    /// <summary>Malformed query string, unparseable body, or an empty batch.</summary>
    BadRequest,

    /// <summary>Unknown source id, or a key that does not match the configured one.</summary>
    Unauthorized,

    /// <summary>Too many imports already in flight. Raise the uploader's spacing.</summary>
    GateFull,

    /// <summary>The write lock did not come free in five seconds.</summary>
    WriteTimeout,

    /// <summary>Body over the size limit, or more findings than one batch accepts.</summary>
    TooLarge
}

/// <summary>
///     Counts refused imports, in process. Deliberately not in SQLite: a refusal
///     happens on the paths that could not take the write lock, so recording it
///     would need the very lock that failed. The count resets on restart, which is
///     what a Prometheus counter is allowed to do.
/// </summary>
public sealed class ImportMetrics
{
    private static readonly ImportRejection[] Reasons = Enum.GetValues<ImportRejection>();
    private readonly long[] _counts = new long[Reasons.Length];

    public void Rejected(ImportRejection reason)
    {
        Interlocked.Increment(ref _counts[(int)reason]);
    }

    /// <summary>
    ///     Every reason with its count, including the zeros. A series that appears
    ///     only once a failure has happened reads as a scrape gap rather than as a
    ///     healthy zero, and an alert on it cannot distinguish the two.
    /// </summary>
    public IEnumerable<(string Reason, long Count)> Snapshot()
    {
        foreach (var reason in Reasons)
            yield return (Label(reason), Interlocked.Read(ref _counts[(int)reason]));
    }

    private static string Label(ImportRejection reason)
    {
        return reason switch
        {
            ImportRejection.BadRequest => "bad_request",
            ImportRejection.Unauthorized => "unauthorized",
            ImportRejection.GateFull => "gate_full",
            ImportRejection.WriteTimeout => "write_timeout",
            ImportRejection.TooLarge => "too_large",
            // No catch-all: a reason added without a label here must fail the build
            // rather than publish refusals under a name that says nothing.
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
        };
    }
}

/// <summary>
///     The import endpoint's admission control: the concurrency gate that decides
///     whether a request runs at all, and the counter that records every refusal.
///     One parameter rather than two because a refusal always touches both.
/// </summary>
public sealed record ImportAdmission(ImportGate Gate, ImportMetrics Metrics)
{
    /// <summary>Counts a refused import and hands its result straight back.</summary>
    public T Refuse<T>(ImportRejection reason, T result)
    {
        Metrics.Rejected(reason);
        return result;
    }
}
