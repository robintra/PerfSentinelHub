using System.Text.Json;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

/// <summary>
///     Writes stored incidents on the wire: the daemon's document as it was
///     captured, then what the Hub knows about it. Hand-written for the same
///     reason the other relayed shapes are, a Utf8JsonWriter keeps it off
///     HubJsonContext and relays every field a later daemon adds.
/// </summary>
public static class IncidentWriter
{
    /// <summary>
    ///     The listing, without the per-incident findings, which can run to a
    ///     thousand rows each: the rows come from a read that never loads them.
    /// </summary>
    public static async Task WriteArrayAsync(
        HttpResponse response,
        IReadOnlyList<StoredIncident> rows,
        IReadOnlyList<SourceOptions> sources,
        CancellationToken cancellationToken)
    {
        response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(response.BodyWriter);
        writer.WriteStartArray();
        foreach (var row in rows)
        {
            WriteIncident(writer, row, sources);
            if (writer.BytesPending < 64 * 1024)
                continue;

            await writer.FlushAsync(cancellationToken);
            await response.BodyWriter.FlushAsync(cancellationToken);
        }

        writer.WriteEndArray();
        await writer.FlushAsync(cancellationToken);
        await response.BodyWriter.FlushAsync(cancellationToken);
    }

    /// <summary>One incident whole, the findings the row was read with included.</summary>
    public static async Task WriteObjectAsync(
        HttpResponse response,
        StoredIncident row,
        IReadOnlyList<SourceOptions> sources,
        CancellationToken cancellationToken)
    {
        response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(response.BodyWriter);
        WriteIncident(writer, row, sources);
        await writer.FlushAsync(cancellationToken);
        await response.BodyWriter.FlushAsync(cancellationToken);
    }

    /// <summary>
    ///     The daemon's reading of `oldest_finding_ms`: at or below the window's
    ///     start the ring still reached the whole window, above it part of the
    ///     window had already been evicted, and absent means the ring was empty.
    /// </summary>
    public static string Capture(long? oldestFindingMs, long windowFromMs)
    {
        return oldestFindingMs switch
        {
            null => "empty",
            var oldest when oldest <= windowFromMs => "complete",
            _ => "partial"
        };
    }

    // `ended_at_ms` comes from the column and not from the document: an end the
    // daemon reported on a re-capture this row did not adopt is still an end.
    // The findings are relayed as the daemon wrote them, with no second parse.
    private static void WriteIncident(
        Utf8JsonWriter writer,
        StoredIncident row,
        IReadOnlyList<SourceOptions> sources)
    {
        using var document = JsonDocument.Parse(row.IncidentJson);
        writer.WriteStartObject();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name == "ended_at_ms")
                continue;
            property.WriteTo(writer);
        }

        if (row.FindingsJson is not null)
        {
            writer.WritePropertyName("findings");
            writer.WriteRawValue(row.FindingsJson);
        }

        JsonWrite.NumberOrNull(writer, "ended_at_ms", row.EndedAtMs);
        var source = sources.FirstOrDefault(candidate => string.Equals(candidate.Id, row.SourceId, StringComparison.Ordinal));
        writer.WriteString("source_id", row.SourceId);
        JsonWrite.StringOrNull(writer, "source_name", source?.Name);
        JsonWrite.StringOrNull(writer, "environment", source?.Environment);
        writer.WriteNumber("first_seen", row.FirstSeenMs);
        writer.WriteNumber("last_seen", row.LastSeenMs);
        writer.WriteNumber("finding_count", row.FindingCount);
        writer.WriteString("capture", Capture(row.OldestFindingMs, row.WindowFromMs));
        writer.WriteEndObject();
    }
}
