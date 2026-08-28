using System.Text.Json;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

public static partial class FindingEnvelopeWriter
{
    public static async Task WriteArrayAsync(
        HttpResponse response,
        IReadOnlyList<StoredFinding> rows,
        DateTimeOffset now,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(response.BodyWriter);
        writer.WriteStartArray();

        foreach (var row in rows)
        {
            // Parsed before anything is written: a throw here would abort a
            // body already partly flushed, and the client would see a truncated
            // array behind a 200. A row whose envelope is unreadable is skipped
            // rather than allowed to take the page down with it.
            JsonDocument envelope;
            try
            {
                envelope = JsonDocument.Parse(row.EnvelopeJson);
            }
            catch (JsonException exception)
            {
                // Dropping it silently would make a finding disappear from the
                // page with nothing anywhere saying a row was unreadable.
                LogUnreadableEnvelope(logger, exception, row.Signature);
                continue;
            }

            using var document = envelope;
            writer.WriteStartObject();
            WriteOriginalProperties(writer, document.RootElement);

            writer.WriteNumber("first_seen", row.FirstSeenMs);
            writer.WriteNumber("last_seen", row.LastSeenMs);
            writer.WriteString("max_confidence", row.MaxConfidence);
            // Derived, never stored: active within the grace window,
            // likely_resolved when the endpoint still heartbeats from a
            // reachable source without the finding, not_observed otherwise.
            writer.WriteString("status", row.Status);
            if (row.Lineage is { } lineage)
            {
                writer.WriteStartObject("lineage");
                writer.WriteNumber("original_first_seen", lineage.OriginalFirstSeenMs);
                writer.WriteNumber("predecessors", lineage.Predecessors);
                writer.WriteEndObject();
            }
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

    [LoggerMessage(1800, LogLevel.Error, "Finding {Signature} has an unreadable envelope and was skipped.")]
    private static partial void LogUnreadableEnvelope(ILogger logger, Exception exception, string signature);

    private static void WriteOriginalProperties(Utf8JsonWriter writer, JsonElement envelope)
    {
        // LINQ would box JsonElement.ObjectEnumerator and allocate on every envelope.
        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
        foreach (var property in envelope.EnumerateObject())
        {
            if (property.Name is not ("first_seen" or "last_seen" or "max_confidence" or "sources"
                or "status" or "lineage"))
                property.WriteTo(writer);
        }
    }
}
