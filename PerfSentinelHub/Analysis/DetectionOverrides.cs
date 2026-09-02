using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PerfSentinelHub.Analysis;

/// <summary>
///     One detection setting the launcher may offer, as the status API states
///     it. <see cref="Default" /> is the engine's own default as a JSON value,
///     relayed rather than modelled, the way the daemon defaults beside it are:
///     an integer, a decimal and a quoted choice ride the same field.
/// </summary>
public sealed record KnobSpec(
    string Name,
    string Kind,
    double? Min,
    double? Max,
    JsonElement Default,
    IReadOnlyList<string>? Choices);

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

    /// <summary>Each integer knob: wire name, engine minimum, Hub maximum, engine default.</summary>
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
    ///     offer one to a binary that predates it.
    /// </summary>
    private static readonly (string Name, double Min, double Max, double Default, string Since)[] Decimals =
    [
        // The engine only asks for a positive number. The floor is the Hub's:
        // below a hundredth, any jitter at all reads as an N+1, which is no
        // threshold. The ceiling is the engine's own.
        ("sanitizer_aware_min_cv", 0.01, 10, 0.5, "0.18.0")
    ];

    /// <summary>Each choice knob, its choices with the engine default first, and the minor that first read it.</summary>
    private static readonly (string Name, string[] Choices, string Since)[] Choices =
    [
        ("sanitizer_aware_classification", ["auto", "strict", "always", "never"], "0.18.0")
    ];

    // TOML literals, formatted when read, so the file is a concatenation.
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public bool IsEmpty => _values.Count == 0;

    /// <summary>Every knob, for the daemon defaults, which any engine version is compared against.</summary>
    public static IEnumerable<KnobSpec> Schema => Describe(null, gate: false);

    /// <summary>Wire order: the integer thresholds first, as they have always been written.</summary>
    private static IEnumerable<string> WireOrder =>
        Knobs.Select(knob => knob.Name)
            .Concat(Decimals.Select(knob => knob.Name))
            .Concat(Choices.Select(knob => knob.Name));

    /// <summary>
    ///     The knobs to offer an engine of that version. One added in a later minor
    ///     is withheld until the probed binary is at least that minor, and a version
    ///     the probe could not read promises nothing: only the ungated eight.
    /// </summary>
    public static IEnumerable<KnobSpec> SchemaFor(string? engineVersion)
    {
        return Describe(engineVersion, gate: true);
    }

    private static IEnumerable<KnobSpec> Describe(string? engineVersion, bool gate)
    {
        var engine = gate ? ParseEngineVersion(engineVersion) : null;

        foreach (var (name, min, max, @default) in Knobs)
            yield return new KnobSpec(name, "integer", min, max, Literal(@default.ToString(CultureInfo.InvariantCulture)), null);
        foreach (var (name, min, max, @default, since) in Decimals)
            if (!gate || Reads(engine, since))
                yield return new KnobSpec(name, "decimal", min, max, Literal(Decimal(@default)), null);
        foreach (var (name, choices, since) in Choices)
            if (!gate || Reads(engine, since))
                yield return new KnobSpec(name, "choice", null, null, Literal(Quoted(choices[0])), choices);
    }

    private static bool Reads(Version? engine, string since)
    {
        return engine is not null && engine >= Version.Parse(since);
    }

    // clap prints `0.18.0`, or `0.18.0-rc.1` for a pre-release, which reads the
    // same keys as the release it precedes. System.Version knows neither suffix.
    private static Version? ParseEngineVersion(string? engineVersion)
    {
        var release = engineVersion?.Split('-', '+')[0];
        return release is not null && Version.TryParse(release, out var version) ? version : null;
    }

    private static JsonElement Literal(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
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
    ///     One submitted value as the TOML literal to write, null when it equals the
    ///     engine's default. False, with <paramref name="error" /> set, when refused.
    /// </summary>
    private static bool TryReadOne(JsonProperty property, out string? literal, out string? error)
    {
        var knob = Array.Find(Knobs, candidate => candidate.Name == property.Name);
        if (knob.Name is not null)
            return TryReadInteger(knob, property.Value, out literal, out error);

        var decimalKnob = Array.Find(Decimals, candidate => candidate.Name == property.Name);
        if (decimalKnob.Name is not null)
            return TryReadDecimal(decimalKnob, property.Value, out literal, out error);

        var choice = Array.Find(Choices, candidate => candidate.Name == property.Name);
        if (choice.Name is not null)
            return TryReadChoice(choice, property.Value, out literal, out error);

        literal = null;
        error = $"Unknown detection setting: {property.Name}.";
        return false;
    }

    private static bool TryReadInteger(
        (string Name, int Min, int Max, int Default) knob,
        JsonElement value,
        out string? literal,
        out string? error)
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

        if (number != knob.Default)
            literal = number.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryReadDecimal(
        (string Name, double Min, double Max, double Default, string Since) knob,
        JsonElement value,
        out string? literal,
        out string? error)
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
            error = $"{knob.Name} must be between {Decimal(knob.Min)} and {Decimal(knob.Max)}.";
            return false;
        }

        if (number != knob.Default)
            literal = Decimal(number);
        return true;
    }

    private static bool TryReadChoice(
        (string Name, string[] Choices, string Since) knob,
        JsonElement value,
        out string? literal,
        out string? error)
    {
        literal = null;
        error = null;
        var chosen = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (chosen is null || !knob.Choices.Contains(chosen, StringComparer.Ordinal))
        {
            error = $"{knob.Name} must be one of {string.Join(", ", knob.Choices)}.";
            return false;
        }

        if (chosen != knob.Choices[0])
            literal = Quoted(chosen);
        return true;
    }

    /// <summary>A float with its point kept, so TOML reads it as one and not as an integer.</summary>
    private static string Decimal(double value)
    {
        return value.ToString("0.0###############", CultureInfo.InvariantCulture);
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
        foreach (var name in WireOrder)
            if (_values.TryGetValue(name, out var literal))
                toml.Append(name).Append(" = ").Append(literal).Append('\n');
        return toml.ToString();
    }
}
