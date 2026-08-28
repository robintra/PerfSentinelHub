using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PerfSentinelHub.Analysis;

/// <summary>
/// Detection thresholds an operator chose to override for one run.
///
/// These decide what the engine calls a problem, not how large the report is.
/// Raising one does not compress anything: it stops the detector from looking.
/// Every override is recorded on the run so two reports produced with different
/// thresholds are never silently compared.
///
/// Bounds mirror the engine's own validator (config/validate.rs
/// validate_detection_params). Upper bounds are the Hub's: the engine leaves
/// most of them open, and a value in the billions is a typo, not a setting.
/// </summary>
public sealed record DetectionOverrides
{
    private const int MaxOccurrences = 10_000;
    private const int MaxDurationMs = 3_600_000;

    /// <summary>Each knob: wire name, engine minimum, Hub maximum, engine default.</summary>
    private static readonly (string Name, int Min, int Max, int Default)[] Knobs =
    [
        ("n_plus_one_min_occurrences", 1, MaxOccurrences, 5),
        ("window_duration_ms", 1, MaxDurationMs, 500),
        ("slow_query_threshold_ms", 1, MaxDurationMs, 500),
        ("slow_query_min_occurrences", 1, MaxOccurrences, 3),
        ("max_fanout", 1, 100_000, 20),
        ("chatty_service_min_calls", 1, MaxOccurrences, 15),
        ("pool_saturation_concurrent_threshold", 2, MaxOccurrences, 10),
        ("serialized_min_sequential", 2, MaxOccurrences, 3)
    ];

    private readonly Dictionary<string, int> _values = new(StringComparer.Ordinal);

    public bool IsEmpty => _values.Count == 0;

    /// <summary>The overrides that differ from the engine's defaults, in wire order.</summary>
    public IEnumerable<KeyValuePair<string, int>> Values =>
        Knobs.Where(knob => _values.ContainsKey(knob.Name))
            .Select(knob => new KeyValuePair<string, int>(knob.Name, _values[knob.Name]));

    /// <summary>The engine's own default for a knob, for the launcher to show.</summary>
    public static IEnumerable<(string Name, int Min, int Max, int Default)> Schema => Knobs;

    /// <summary>
    /// Reads the overrides an operator submitted. A value equal to the engine's
    /// default is dropped rather than written out, so a run only records what
    /// actually departs from the standard configuration.
    /// </summary>
    public static DetectionOverrides? TryParse(JsonElement payload, out string? error)
    {
        error = null;
        var overrides = new DetectionOverrides();
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return overrides;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "Detection overrides must be a JSON object.";
            return null;
        }

        foreach (var property in payload.EnumerateObject())
        {
            var knob = Array.Find(Knobs, candidate => candidate.Name == property.Name);
            if (knob.Name is null)
            {
                error = $"Unknown detection setting: {property.Name}.";
                return null;
            }

            if (property.Value.ValueKind != JsonValueKind.Number ||
                !property.Value.TryGetInt32(out var value))
            {
                error = $"{knob.Name} must be a whole number.";
                return null;
            }

            if (value < knob.Min || value > knob.Max)
            {
                error = $"{knob.Name} must be between {knob.Min} and {knob.Max}.";
                return null;
            }

            if (value != knob.Default)
                overrides._values[knob.Name] = value;
        }

        return overrides;
    }

    /// <summary>
    /// The `[detection]` section to hand the engine through `-c`. Only the
    /// overridden keys appear, so every other threshold keeps the engine's own
    /// documented default rather than one this file froze.
    /// </summary>
    public string ToToml()
    {
        var toml = new StringBuilder("[detection]\n");
        foreach (var (name, value) in Values)
            toml.Append(name).Append(" = ").Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return toml.ToString();
    }
}
