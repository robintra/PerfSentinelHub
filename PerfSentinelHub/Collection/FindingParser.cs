using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PerfSentinelHub.Collection;

public sealed record ParsedBatch(IReadOnlyList<ParsedFinding> Findings, int RejectedCount);

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
    int ConfidenceRank);

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
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The findings response must be a JSON array.");

            var findings = new List<ParsedFinding>();
            var rejected = 0;
            foreach (var envelope in document.RootElement.EnumerateArray())
            {
                if (TryParse(envelope, out var finding))
                    findings.Add(finding);
                else
                    rejected++;
            }

            return new ParsedBatch(findings, rejected);
        }
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

        string? traceId = null;
        if (body.TryGetProperty("trace_id", out var trace) && trace.ValueKind == JsonValueKind.String)
            traceId = trace.GetString();

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
            ConfidenceRank(confidence));
        return true;
    }

    private static bool TryString(JsonElement element, string propertyName, out string value)
    {
        value = "";
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString() ?? "";
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
