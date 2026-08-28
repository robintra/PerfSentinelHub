using System.Text.Json;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

/// <summary>
/// Writes runs on the wire. `request` and `result` are stored as JSON text and
/// are re-emitted verbatim rather than round-tripped through a model: their
/// shape varies with the source kind and belongs to the launcher's contract.
/// </summary>
public static class AnalysisRunWriter
{
    public static async Task WriteArrayAsync(
        HttpResponse response,
        IReadOnlyList<AnalysisRun> runs,
        CancellationToken cancellationToken)
    {
        response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(response.BodyWriter);
        writer.WriteStartArray();
        foreach (var run in runs)
            WriteRun(writer, run);
        writer.WriteEndArray();
        await writer.FlushAsync(cancellationToken);
        await response.BodyWriter.FlushAsync(cancellationToken);
    }

    public static async Task WriteObjectAsync(
        HttpResponse response,
        AnalysisRun run,
        CancellationToken cancellationToken)
    {
        response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(response.BodyWriter);
        WriteRun(writer, run);
        await writer.FlushAsync(cancellationToken);
        await response.BodyWriter.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// The stored form of a run's result. Written by hand so the wire names
    /// stay snake_case without threading a serializer policy through the
    /// source-generated context.
    /// </summary>
    public static string SerializeSummary(ReportSummary summary)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("empty", summary.Empty);
            writer.WriteNumber("findings", summary.Findings);
            writer.WriteNumber("critical", summary.Critical);
            writer.WriteNumber("warning", summary.Warning);
            writer.WriteNumber("info", summary.Info);
            writer.WriteNumber("traces_analyzed", summary.TracesAnalyzed);
            writer.WriteBoolean("quality_gate_passed", summary.QualityGatePassed);
            writer.WriteStartArray("warnings");
            foreach (var warning in summary.Warnings)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", warning.Kind);
                writer.WriteString("message", warning.Message);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteRun(Utf8JsonWriter writer, AnalysisRun run)
    {
        writer.WriteStartObject();
        writer.WriteString("id", run.Id);
        writer.WriteString("status", run.Status);
        writer.WriteString("source_id", run.SourceId);
        writer.WriteString("source_name", run.SourceName);
        writer.WriteString("environment", run.Environment);
        writer.WriteString("kind", run.Kind);
        WriteRawOrNull(writer, "request", run.RequestJson);
        writer.WriteString("requested_by", run.RequestedBy);
        writer.WriteNumber("created_at_ms", run.CreatedAtMs);
        WriteNumberOrNull(writer, "started_at_ms", run.StartedAtMs);
        WriteNumberOrNull(writer, "finished_at_ms", run.FinishedAtMs);
        WriteNumberOrNull(writer, "expires_at_ms", run.ExpiresAtMs);
        WriteStringOrNull(writer, "producer_version", run.ProducerVersion);
        WriteStringOrNull(writer, "error_code", run.ErrorCode);
        WriteRawOrNull(writer, "result", run.ResultJson);
        writer.WriteEndObject();
    }

    private static void WriteRawOrNull(Utf8JsonWriter writer, string name, string? json)
    {
        if (json is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WritePropertyName(name);
        using var document = JsonDocument.Parse(json);
        document.RootElement.WriteTo(writer);
    }

    private static void WriteNumberOrNull(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } number)
            writer.WriteNumber(name, number);
        else
            writer.WriteNull(name);
    }

    private static void WriteStringOrNull(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }
}
