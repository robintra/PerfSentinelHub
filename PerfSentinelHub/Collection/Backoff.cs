namespace PerfSentinelHub.Collection;

public static class Backoff
{
    public static TimeSpan Delay(int failures, double sample)
    {
        var exponent = Math.Min(Math.Max(0, failures - 1), 9);
        var baseMs = Math.Min(300_000d, 1_000d * Math.Pow(2, exponent));
        var jitter = 0.8d + 0.4d * Math.Clamp(sample, 0d, 1d);
        return TimeSpan.FromMilliseconds(Math.Round(baseMs * jitter));
    }
}
