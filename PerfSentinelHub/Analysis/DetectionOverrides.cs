using System.Globalization;
using System.Text;
using System.Text.Json;
using PerfSentinelHub.Api;

namespace PerfSentinelHub.Analysis;

/// <summary>
///     Detection thresholds an operator chose to override for one run.
///     These decide what the engine calls a problem, not how large the report is.
///     Raising one does not compress anything: it stops the detector from looking.
///     Every override is recorded on the run so two reports produced with different
///     thresholds are never silently compared.
///     Bounds mirror the engine's own validator (config/validate.rs
///     validate_detection_params). Upper bounds are the Hub's: the engine leaves
///     most of them open, and a value in the billions is a typo, not a setting.
/// </summary>
public sealed record DetectionOverrides
{
    private const int MaxOccurrences = 10_000;
    private const int MaxDurationMs = 3_600_000;

    /// <summary>Each integer knob: wire name, engine minimum, Hub maximum, engine default. Read by every engine the Hub can drive.</summary>
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

    /// <summary>
    ///     Each decimal knob, with the engine minor that first read it: an older
    ///     `[detection]` refuses an unknown key outright, so the launcher must not
    ///     offer one to a binary that predates it. The minor is compared to what
    ///     `--version` prints, and the engine bumps its version last, so a build of
    ///     an unreleased branch still reports the previous minor and is withheld a
    ///     knob it reads until the bump lands.
    /// </summary>
    private static readonly (string Name, double Min, double Max, double Default, string Since)[] Decimals =
    [
        // The engine only asks for a positive number. The floor is the Hub's:
        // below a hundredth, any jitter at all reads as an N+1, which is no
        // threshold. The ceiling is the engine's own.
        ("sanitizer_aware_min_cv", 0.01, 10, 0.5, "0.18.0")
    ];

    /// <summary>Each choice knob, its choices with the engine default first, and the minor that first read it, null when every engine does.</summary>
    private static readonly (string Name, string[] Choices, string? Since)[] Choices =
    [
        // The mode has been read since 0.5.7, before any engine this Hub drives, so
        // it is not gated: a probe that could not read the version must still offer it.
        ("sanitizer_aware_classification", ["auto", "strict", "always", "never"], null)
    ];

    /// <summary>
    ///     The three tables as one list, built once: what the status API serves,
    ///     what a submission is checked against, and the order the file is written
    ///     in. `Since` is null for a knob every engine reads.
    /// </summary>
    private static readonly (DetectionKnob Knob, Version? Since)[] All = BuildAll();

    // TOML literals, formatted when read, so the file is a concatenation.
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public bool IsEmpty => _values.Count == 0;

    /// <summary>Every knob, for the daemon defaults, which any engine version is compared against.</summary>
    public static IEnumerable<DetectionKnob> Schema => All.Select(entry => entry.Knob);

    /// <summary>
    ///     The knobs to offer an engine of that version. One added in a later minor
    ///     is withheld until the probed binary is at least that minor, and a version
    ///     the probe could not read promises nothing: only the ungated eight.
    /// </summary>
    public static IEnumerable<DetectionKnob> SchemaFor(string? engineVersion)
    {
        var engine = ParseEngineVersion(engineVersion);
        return All.Where(entry => Reads(engine, entry.Since)).Select(entry => entry.Knob);
    }

    private static (DetectionKnob, Version?)[] BuildAll()
    {
        var all = new List<(DetectionKnob, Version?)>();
        foreach (var (name, min, max, @default) in Knobs)
        {
            var literal = JsonRead.Literal(@default.ToString(CultureInfo.InvariantCulture));
            all.Add((new DetectionKnob(name, "integer", min, max, literal, null), null));
        }

        foreach (var (name, min, max, @default, since) in Decimals)
            all.Add((new DetectionKnob(name, "decimal", min, max, JsonRead.Literal(Decimal(@default)), null), Version.Parse(since)));
        foreach (var (name, choices, since) in Choices)
        {
            // A copy, so the served list cannot reach back into the table.
            var knob = new DetectionKnob(name, "choice", null, null, JsonRead.Literal(Quoted(choices[0])), [.. choices]);
            all.Add((knob, since is null ? null : Version.Parse(since)));
        }

        return [.. all];
    }

    private static bool Reads(Version? engine, Version? since)
    {
        return since is null || (engine is not null && engine >= since);
    }

    // clap prints `0.18.0`, or `0.18.0-rc.1` for a pre-release, which reads the
    // same keys as the release it precedes. System.Version knows neither suffix.
    private static Version? ParseEngineVersion(string? engineVersion)
    {
        var release = engineVersion?.Split('-', '+')[0];
        return release is not null && Version.TryParse(release, out var version) ? version : null;
    }

    /// <summary>
    ///     Reads the overrides an operator submitted. A value equal to the engine's
    ///     default is dropped rather than written out, so a run only records what
    ///     actually departs from the standard configuration.
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
            if (!TryReadOne(property, out var literal, out error))
                return null;
            if (literal is not null)
                overrides._values[property.Name] = literal;
        }

        return overrides;
    }

    /// <summary>
    ///     The reason this run must not be queued for an engine of that version, or
    ///     null when every override is one it reads. Checked at submission, where
    ///     the version is known and a 400 can name it, rather than found by the
    ///     engine at run time with a stderr the Hub never returns.
    /// </summary>
    public string? RefusedBy(string? engineVersion)
    {
        var engine = ParseEngineVersion(engineVersion);
        foreach (var (knob, since) in All)
            if (_values.ContainsKey(knob.Name) && !Reads(engine, since))
                return $"{knob.Name} needs engine {since} or later, {Runs(engineVersion, engine)}.";
        return null;
    }

    /// <summary>What the Hub knows of its engine's version, saying so when it could not read it.</summary>
    private static string Runs(string? engineVersion, Version? engine)
    {
        if (engineVersion is null)
            return "this Hub has not read its engine's version";
        return engine is null
            ? $"this Hub could not read its engine's version ({engineVersion})"
            : $"this Hub runs {engineVersion}";
    }

    /// <summary>
    ///     One submitted value as the TOML literal to write, null when it equals the
    ///     engine's default. False, with <paramref name="error" /> set, when refused.
    /// </summary>
    private static bool TryReadOne(JsonProperty property, out string? literal, out string? error)
    {
        var index = Array.FindIndex(All, entry => entry.Knob.Name == property.Name);
        if (index < 0)
        {
            literal = null;
            error = $"Unknown detection setting: {property.Name}.";
            return false;
        }

        var knob = All[index].Knob;
        return knob.Kind switch
        {
            "integer" => TryReadInteger(knob, property.Value, out literal, out error),
            "decimal" => TryReadDecimal(knob, property.Value, out literal, out error),
            "choice" => TryReadChoice(knob, property.Value, out literal, out error),
            // A kind the table declares and nothing here reads is a programming
            // error, not a bad request, and must not fall into TryReadChoice.
            _ => throw new InvalidOperationException($"No reader for knob kind {knob.Kind} on {knob.Name}.")
        };
    }

    private static bool TryReadInteger(DetectionKnob knob, JsonElement value, out string? literal, out string? error)
    {
        literal = null;
        error = null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            error = $"{knob.Name} must be a whole number.";
            return false;
        }

        if (number < knob.Min || number > knob.Max)
        {
            error = $"{knob.Name} must be between {knob.Min} and {knob.Max}.";
            return false;
        }

        if (number != knob.Default.GetInt32())
            literal = number.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryReadDecimal(DetectionKnob knob, JsonElement value, out string? literal, out string? error)
    {
        literal = null;
        error = null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
        {
            error = $"{knob.Name} must be a number.";
            return false;
        }

        if (number < knob.Min || number > knob.Max)
        {
            error = $"{knob.Name} must be between {Decimal(knob.Min!.Value)} and {Decimal(knob.Max!.Value)}.";
            return false;
        }

        // Compared as the text that would be written rather than as doubles: the
        // question is whether this run would spell the knob exactly as the
        // default already does, and an exact equality on two doubles is the
        // wrong instrument for it.
        var text = Decimal(number);
        if (text != Decimal(knob.Default.GetDouble()))
            literal = text;
        return true;
    }

    private static bool TryReadChoice(DetectionKnob knob, JsonElement value, out string? literal, out string? error)
    {
        literal = null;
        error = null;
        var chosen = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (chosen is null || !knob.Choices!.Contains(chosen, StringComparer.Ordinal))
        {
            error = $"{knob.Name} must be one of {string.Join(", ", knob.Choices!)}.";
            return false;
        }

        if (chosen != knob.Default.GetString())
            literal = Quoted(chosen);
        return true;
    }

    /// <summary>
    ///     The shortest text that reads back to the same double, with a point kept
    ///     so TOML reads a float rather than an integer. A fixed-digit pattern would
    ///     round past fifteen significant digits and write a value the run never had.
    /// </summary>
    private static string Decimal(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') ? text : text + ".0";
    }

    /// <summary>TOML has no bare words: a choice is a basic string. The choices carry nothing to escape.</summary>
    private static string Quoted(string value)
    {
        return "\"" + value + "\"";
    }

    /// <summary>
    ///     The `[detection]` section to hand the engine through `-c`. Only the
    ///     overridden keys appear, so every other threshold keeps the engine's own
    ///     documented default rather than one this file froze.
    /// </summary>
    public string ToToml()
    {
        var toml = new StringBuilder("[detection]\n");
        foreach (var (knob, _) in All)
            if (_values.TryGetValue(knob.Name, out var literal))
                toml.Append(knob.Name).Append(" = ").Append(literal).Append('\n');
        return toml.ToString();
    }
}
