using System.Reflection;

namespace PerfSentinelHub.Api;

/// <summary>
///     The version the Hub reports about itself, on `/api/status` and on the
///     `build_info` metric. Read once, from one place, so the two cannot disagree.
/// </summary>
public static class HubVersion
{
    /// <summary>
    ///     AssemblyVersion is padded to four components, so it reads `0.1.2.0`
    ///     where the tag, the chart appVersion and the image label all say
    ///     `0.1.2`. The informational version carries what was released.
    /// </summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(HubVersion).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational)) return assembly.GetName().Version?.ToString() ?? "unknown";

        // SourceLink appends "+<commit>", which is provenance and not a version.
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
