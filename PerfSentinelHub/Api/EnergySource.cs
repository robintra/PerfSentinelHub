using System.Text.Json;

namespace PerfSentinelHub.Api;

/// <summary>
///     Where a snapshot's energy figure comes from, worded as the engine words it:
///     a port of `GreenSummary::energy_source_label` (perf-sentinel 0.26.0), rules
///     and strings alike. It reads per-service coverage and never `energy_model`,
///     which on an Electricity Maps daemon names the intensity source and on any
///     other the intensity tier.
/// </summary>
public static class EnergySource
{
    private const string CalSuffix = "+cal";
    private const string Modeled = "modeled from I/O counts";
    private const string Calibrated = " · calibrated";

    /// <summary>Null when the snapshot computed no energy.</summary>
    public static string? Label(JsonElement green)
    {
        if (!green.TryGetProperty("energy_kwh", out var kwh) ||
            kwh.ValueKind != JsonValueKind.Number ||
            kwh.GetDouble() <= 0.0)
            return null;

        var models = StringMap(green, "per_service_energy_model");
        var ratios = new List<(string Service, double Ratio)>();
        if (green.TryGetProperty("per_service_measured_ratio", out var map) && map.ValueKind == JsonValueKind.Object)
            foreach (var entry in map.EnumerateObject())
                // A ratio the engine could not write reads as unmeasured, as NaN does there.
                ratios.Add((entry.Name,
                    entry.Value.ValueKind == JsonValueKind.Number ? entry.Value.GetDouble() : double.NaN));

        // The flag survives a measured window tag, the suffix covers daemons older than it.
        var calibrated = (green.TryGetProperty("energy_calibrated", out var flag) &&
                          flag.ValueKind == JsonValueKind.True) ||
                         (JsonRead.ReadString(green, "energy_model") ?? "").EndsWith(CalSuffix, StringComparison.Ordinal) ||
                         models.Values.Any(m => m.EndsWith(CalSuffix, StringComparison.Ordinal));
        var cal = calibrated ? Calibrated : "";

        var covered = ratios.Where(r => r.Ratio > 0.0).Select(r => r.Service).ToList();
        if (covered.Count == 0)
            return Modeled + cal;

        var tags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var service in covered)
            if (models.TryGetValue(service, out var model) && DisplayTag(model) is { } tag)
                tags.Add(tag);
        var named = tags.Count == 0 ? "unknown" : string.Join(", ", tags);

        return ratios.All(r => r.Ratio >= 1.0)
            ? "source " + named
            : $"source {named} on {covered.Count} of {ratios.Count} services · rest {Modeled}{cal}";
    }

    /// <summary>
    ///     A tag without its `+cal` suffix, or null when it is not a short
    ///     `[A-Za-z0-9_+-]` token: the daemon is a source the Hub does not control.
    /// </summary>
    internal static string? DisplayTag(string tag)
    {
        if (tag.EndsWith(CalSuffix, StringComparison.Ordinal))
            tag = tag[..^CalSuffix.Length];
        return tag.Length is >= 1 and <= 64 && tag.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '+' or '-')
            ? tag
            : null;
    }

    private static Dictionary<string, string> StringMap(JsonElement green, string name)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (green.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
            foreach (var entry in value.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.String)
                    map[entry.Name] = entry.Value.GetString()!;
        return map;
    }
}
