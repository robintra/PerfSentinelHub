using System.Text.Json;

namespace PerfSentinelHub.Analysis;

/// <summary>
/// A scope caveat attached to a result. Not a closed vocabulary, unlike the
/// error codes: the engine adds kinds between minors.
/// </summary>
public sealed record ResultWarning(string Kind, string Message);

/// <summary>
/// What a run produced, read back from the engine's own report JSON. The Hub
/// stores this rather than the whole report: the report is the HTML file, and
/// this is what a list of runs shows without opening one.
/// </summary>
public sealed record ReportSummary(
    bool Empty,
    int Findings,
    int Critical,
    int Warning,
    int Info,
    int TracesAnalyzed,
    bool QualityGatePassed,
    IReadOnlyList<ResultWarning> Warnings)
{
    /// <summary>
    /// How many findings the rendered report actually holds, when the sink had
    /// to drop some to fit its budget. Null when nothing was dropped. It cannot
    /// come from the summary above: that is parsed from the engine's pre-trim
    /// output, and only the rendered artefact knows what survived.
    /// </summary>
    public int? KeptFindings { get; set; }

    /// <summary>
    /// The rendered file's size on disk. Recorded rather than measured later:
    /// the report is deleted when its retention runs out, and the launcher
    /// shows past weights so an operator sees what this source's traces cost
    /// instead of an estimate the Hub cannot make.
    /// </summary>
    public long? ReportBytes { get; set; }

    // A remote server writes these strings and the launcher renders them.
    // Bounded here so one run cannot carry an unbounded payload into storage.
    private const int MaxWarnings = 20;
    private const int MaxWarningChars = 512;

    /// <summary>
    /// Reads the engine's report JSON. Returns null when the document is not
    /// one, which the caller reports as a failed run rather than an empty one.
    /// </summary>
    public static ReportSummary? TryParse(ReadOnlySpan<byte> reportJson, out string? binaryVersion)
    {
        binaryVersion = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(reportJson.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            binaryVersion = ReadString(root, "binary_version");
            var (findings, critical, warning, info) = CountFindings(root);
            var tracesAnalyzed = ReadTracesAnalyzed(root);
            return new ReportSummary(
                // Zero traces is the engine answering correctly about a source
                // that had nothing to show, not a rendering fault.
                Empty: tracesAnalyzed == 0,
                findings,
                critical,
                warning,
                info,
                tracesAnalyzed,
                ReadQualityGate(root),
                ReadWarnings(root));
        }
    }

    private static (int Findings, int Critical, int Warning, int Info) CountFindings(JsonElement root)
    {
        if (!root.TryGetProperty("findings", out var findings) ||
            findings.ValueKind != JsonValueKind.Array)
            return (0, 0, 0, 0);

        int total = 0, critical = 0, warning = 0, info = 0;
        foreach (var finding in findings.EnumerateArray())
        {
            total++;
            switch (ReadString(finding, "severity"))
            {
                case "critical": critical++; break;
                case "warning": warning++; break;
                case "info": info++; break;
                default: break;
            }
        }

        return (total, critical, warning, info);
    }

    private static int ReadTracesAnalyzed(JsonElement root) =>
        root.TryGetProperty("analysis", out var analysis) &&
        analysis.ValueKind == JsonValueKind.Object &&
        analysis.TryGetProperty("traces_analyzed", out var traces) &&
        traces.ValueKind == JsonValueKind.Number &&
        traces.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static bool ReadQualityGate(JsonElement root) =>
        root.TryGetProperty("quality_gate", out var gate) &&
        gate.ValueKind == JsonValueKind.Object &&
        gate.TryGetProperty("passed", out var passed) &&
        passed.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Prefers the structured `warning_details` and falls back to the legacy
    /// `warnings` array of plain strings, the way the engine's own renderers do.
    /// </summary>
    private static IReadOnlyList<ResultWarning> ReadWarnings(JsonElement root)
    {
        var warnings = new List<ResultWarning>();
        if (root.TryGetProperty("warning_details", out var details) &&
            details.ValueKind == JsonValueKind.Array)
        {
            foreach (var detail in details.EnumerateArray().Take(MaxWarnings))
            {
                if (ReadString(detail, "message") is { } message)
                    warnings.Add(new ResultWarning(
                        Truncate(ReadString(detail, "kind") ?? "unknown"),
                        Truncate(message)));
            }
        }

        if (warnings.Count > 0 ||
            !root.TryGetProperty("warnings", out var legacy) ||
            legacy.ValueKind != JsonValueKind.Array)
            return warnings;

        foreach (var entry in legacy.EnumerateArray().Take(MaxWarnings))
        {
            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { } message)
                warnings.Add(new ResultWarning("unknown", Truncate(message)));
        }

        return warnings;
    }

    private static string Truncate(string value) =>
        value.Length <= MaxWarningChars ? value : value[..MaxWarningChars];

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
