using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PerfSentinelHub.Collection;

public sealed record ParsedBatch(IReadOnlyList<ParsedFinding> Findings, int RejectedCount);

public sealed record ParsedImport(string ProducerVersion, ParsedBatch Batch);

public sealed class ImportBatchTooLargeException : Exception;

public sealed record ParsedFinding(
    string Signature,
    string EnvelopeJson,
    string Service,
    string FindingType,
    string Severity,
    string Endpoint,
    string TemplateHash,
    string? TraceId,
    string Confidence,
    int ConfidenceRank,
    long? FirstSeenMs,
    long? StoredAtMs);

// Explicit validation branches keep malformed-input handling auditable.
// ReSharper disable ConvertIfStatementToSwitchStatement
// ReSharper disable ConvertIfStatementToReturnStatement
public static class FindingParser
{
    public static ParsedBatch Parse(ReadOnlyMemory<byte> payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The findings response is not valid JSON.", exception);
        }

        using (document)
        {
            return ParseArray(document.RootElement);
        }
    }

    public static ParsedImport ParseImport(ReadOnlyMemory<byte> payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The import body is not valid JSON.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryString(root, "producer_version", out var producerVersion) ||
                producerVersion.Length > 64 ||
                !root.TryGetProperty("findings", out var findings) ||
                findings.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The import body is invalid.");

            var count = findings.GetArrayLength();
            if (count > 100)
                throw new ImportBatchTooLargeException();
            if (count == 0)
                throw new InvalidDataException("The import batch is empty.");

            return new ParsedImport(producerVersion, ParseArray(findings));
        }
    }

    private static ParsedBatch ParseArray(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The findings response must be a JSON array.");

        var findings = new List<ParsedFinding>();
        var rejected = 0;
        foreach (var envelope in root.EnumerateArray())
        {
            if (TryParse(envelope, out var finding))
                findings.Add(finding);
            else
                rejected++;
        }

        return new ParsedBatch(findings, rejected);
    }

    private static bool TryParse(JsonElement envelope, out ParsedFinding finding)
    {
        finding = null!;
        if (envelope.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("finding", out var body) ||
            body.ValueKind != JsonValueKind.Object ||
            !TryString(body, "signature", out var signature) ||
            !TryString(body, "service", out var service) ||
            !TryString(body, "type", out var findingType) ||
            !TryString(body, "severity", out var severity) ||
            !TryString(body, "source_endpoint", out var endpoint) ||
            !TryString(body, "confidence", out var confidence) ||
            !body.TryGetProperty("pattern", out var pattern) ||
            pattern.ValueKind != JsonValueKind.Object ||
            !TryString(pattern, "template", out var template))
            return false;

        var traceId = body.TryGetProperty("trace_id", out var trace) &&
                      trace.ValueKind == JsonValueKind.String
            ? trace.GetString()
            : null;

        finding = new ParsedFinding(
            signature,
            envelope.GetRawText(),
            service,
            findingType,
            severity,
            endpoint,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(template))).ToLowerInvariant(),
            traceId,
            confidence,
            ConfidenceRank(confidence),
            TryPositiveInt64(envelope, "first_seen_ms"),
            TryPositiveInt64(envelope, "stored_at_ms"));
        return true;
    }

    private static long? TryPositiveInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out var value) &&
               value > 0
            ? value
            : null;
    }

    private static bool TryString(JsonElement element, string propertyName, out string value)
    {
        value = element.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";
        return value.Length > 0;
    }

    private static int ConfidenceRank(string confidence) => confidence switch
    {
        "daemon_production" => 4,
        "daemon_staging" => 3,
        "ci_batch" => 2,
        "local_batch" => 1,
        _ => 0
    };
}
// ReSharper restore ConvertIfStatementToReturnStatement
// ReSharper restore ConvertIfStatementToSwitchStatement
