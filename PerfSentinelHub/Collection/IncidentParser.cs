using System.Buffers;
using System.Text;
using System.Text.Json;
using PerfSentinelHub.Api;

namespace PerfSentinelHub.Collection;

/// <summary>
///     One incident as the daemon listed it: the columns the Hub indexes beside
///     the document kept verbatim, the way a finding envelope is. The findings
///     travel apart from the rest of the document, since they can run to a
///     thousand and only the single-incident route re-emits them.
/// </summary>
public sealed record ParsedIncident(
    string Id,
    string Service,
    string Kind,
    long AtMs,
    long? EndedAtMs,
    long WindowFromMs,
    long WindowToMs,
    long? OldestFindingMs,
    int FindingCount,
    // The daemon's document without its `findings` property.
    string IncidentJson,
    // The `findings` array as the daemon wrote it.
    string FindingsJson);

public sealed record ParsedIncidentPage(IReadOnlyList<ParsedIncident> Incidents, int RejectedCount);

public static class IncidentParser
{
    /// <summary>
    ///     The daemon's closed set of kinds. Anything else folds to `other`, as
    ///     the daemon itself does, so a label can never carry a free string.
    /// </summary>
    public static readonly string[] Kinds = ["oom_kill", "memory_saturation", "restart", "deploy", "other"];

    // Unix-ms sanity floor (2001-09-09), the same one the finding parser applies.
    private const long MinPlausibleEpochMs = 1_000_000_000_000;

    public static ParsedIncidentPage Parse(ReadOnlyMemory<byte> payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The incidents response is not valid JSON.", exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The incidents response must be a JSON array.");

            var incidents = new List<ParsedIncident>();
            var rejected = 0;
            foreach (var element in document.RootElement.EnumerateArray())
                if (TryParse(element, out var incident))
                    incidents.Add(incident);
                else
                    rejected++;

            return new ParsedIncidentPage(incidents, rejected);
        }
    }

    private static bool TryParse(JsonElement element, out ParsedIncident incident)
    {
        incident = null!;
        if (element.ValueKind != JsonValueKind.Object ||
            JsonRead.ReadString(element, "id") is not { } id ||
            !IsIncidentId(id) ||
            JsonRead.ReadString(element, "service") is not { Length: > 0 } service ||
            TryEpochMs(element, "at_ms") is not { } atMs ||
            TryEpochMs(element, "window_from_ms") is not { } windowFromMs ||
            TryEpochMs(element, "window_to_ms") is not { } windowToMs ||
            !element.TryGetProperty("findings", out var findings) ||
            findings.ValueKind != JsonValueKind.Array)
            return false;

        incident = new ParsedIncident(
            id,
            service,
            FoldKind(element),
            atMs,
            TryEpochMs(element, "ended_at_ms"),
            windowFromMs,
            windowToMs,
            TryEpochMs(element, "oldest_finding_ms"),
            findings.GetArrayLength(),
            WithoutFindings(element),
            findings.GetRawText());
        return true;
    }

    private static string WithoutFindings(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject())
                if (property.Name != "findings")
                    property.WriteTo(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // The daemon's id is a SHA-256 over service|kind|at_ms, printed as lowercase hex.
    private static bool IsIncidentId(string id)
    {
        if (id.Length != 32)
            return false;
        foreach (var character in id)
            if (!char.IsAsciiHexDigitLower(character))
                return false;
        return true;
    }

    private static string FoldKind(JsonElement element)
    {
        var kind = JsonRead.ReadString(element, "kind");
        return kind is not null && Array.IndexOf(Kinds, kind) >= 0 ? kind : "other";
    }

    private static long? TryEpochMs(JsonElement element, string propertyName)
    {
        return JsonRead.ReadLong(element, propertyName) is { } value && value >= MinPlausibleEpochMs
            ? value
            : null;
    }
}
