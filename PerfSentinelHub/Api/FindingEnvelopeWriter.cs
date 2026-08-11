using System.Text.Json;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

public static class FindingEnvelopeWriter
{
    public static async Task WriteArrayAsync(
        HttpResponse response,
        IReadOnlyList<StoredFinding> rows,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(response.BodyWriter);
        writer.WriteStartArray();

        foreach (var row in rows)
        {
            using var document = JsonDocument.Parse(row.EnvelopeJson);
            writer.WriteStartObject();
            // LINQ would box JsonElement.ObjectEnumerator and allocate on every envelope.
            // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name is "first_seen" or "last_seen" or "max_confidence" or "sources")
                    continue;

                property.WriteTo(writer);
            }

            writer.WriteNumber("first_seen", row.FirstSeenMs);
            writer.WriteNumber("last_seen", row.LastSeenMs);
            writer.WriteString("max_confidence", row.MaxConfidence);
            writer.WriteStartArray("sources");
            foreach (var source in row.Sources.OrderBy(item => item.SourceId, StringComparer.Ordinal))
            {
                var ageSeconds = Math.Max(0, (now.ToUnixTimeMilliseconds() - source.LastSeenMs) / 1000);
                writer.WriteStartObject();
                writer.WriteString("name", source.SourceName);
                writer.WriteString("environment", source.Environment);
                writer.WriteString("producer_version", source.ProducerVersion);
                writer.WriteNumber("age_seconds", ageSeconds);
                writer.WriteString("status", source.UnreachableSinceMs is null ? "ok" : "unreachable_since");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();

            // Flush as we go: a full 10 000-envelope page would otherwise sit in memory at once.
            if (writer.BytesPending < 64 * 1024)
                continue;

            await writer.FlushAsync(cancellationToken);
            await response.BodyWriter.FlushAsync(cancellationToken);
        }

        writer.WriteEndArray();
        await writer.FlushAsync(cancellationToken);
        await response.BodyWriter.FlushAsync(cancellationToken);
    }
}
