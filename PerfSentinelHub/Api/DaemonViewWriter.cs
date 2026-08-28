using System.Text.Json;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Api;

/// <summary>
/// Writes a daemon's own account of itself on the wire.
///
/// The three configuration sections are re-emitted verbatim rather than
/// modelled: the allowlist behind them is the daemon's work, not the Hub's, and
/// a model here would silently drop every field a later engine minor adds.
///
/// Going through a Utf8JsonWriter is also what keeps this off HubJsonContext.
/// Returning a record from the endpoint instead would route serialization back
/// through the ASP.NET options and need a [JsonSerializable] entry, whose
/// absence fails at runtime under AOT publish only and never under dotnet run.
/// </summary>
public static class DaemonViewWriter
{
    public static async Task WriteAsync(
        HttpResponse response,
        DaemonViewData view,
        CancellationToken cancellationToken)
    {
        response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(response.BodyWriter);
        writer.WriteStartObject();
        writer.WriteString("source_id", view.SourceId);
        writer.WriteNumber("observed_at_ms", view.ObservedAtMs);
        writer.WriteString("state", view.State);
        WriteStringOrNull(writer, "error_code", view.ErrorCode);
        WriteStringOrNull(writer, "version", view.Status?.Version);
        WriteNumberOrNull(writer, "uptime_seconds", view.Status?.UptimeSeconds);

        WriteRawOrNull(writer, "config", view.ConfigJson);
        WriteStringOrNull(writer, "config_unavailable_reason", view.ConfigUnavailableReason);
        WriteRawOrNull(writer, "detection_config", view.DetectionConfigJson);
        WriteRawOrNull(writer, "scoring_config", view.ScoringConfigJson);
        WriteStringOrNull(writer, "energy_model", view.EnergyModel);

        var gauges = view.Status is null ? null : DaemonView.Read(view.Status);
        WriteGauge(writer, "traces", gauges?.Traces);
        WriteGauge(writer, "analysis_queue", gauges?.AnalysisQueue);
        WriteGauge(writer, "findings", gauges?.Findings);

        writer.WritePropertyName("warnings");
        writer.WriteStartArray();
        foreach (var warning in view.Warnings)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", warning.Kind);
            writer.WriteString("message", warning.Message);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
        await response.BodyWriter.FlushAsync(cancellationToken);
    }

    private static void WriteGauge(Utf8JsonWriter writer, string name, DaemonGauge? gauge)
    {
        if (gauge is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WritePropertyName(name);
        writer.WriteStartObject();
        WriteNumberOrNull(writer, "value", gauge.Value);
        WriteNumberOrNull(writer, "capacity", gauge.Capacity);
        if (gauge.Pct is { } pct)
            writer.WriteNumber("pct", Math.Round(pct, 1));
        else
            writer.WriteNull("pct");
        writer.WriteBoolean("at_capacity", gauge.AtCapacity);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Parsed before the property name is written: a throw once the writer has
    /// started would abort a body already partly flushed, and the client would
    /// see a truncated object behind a 200.
    /// </summary>
    private static void WriteRawOrNull(Utf8JsonWriter writer, string name, string? json)
    {
        JsonDocument? document = null;
        try
        {
            if (json is not null)
                document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            document = null;
        }

        if (document is null)
        {
            writer.WriteNull(name);
            return;
        }

        using (document)
        {
            writer.WritePropertyName(name);
            document.RootElement.WriteTo(writer);
        }
    }

    private static void WriteStringOrNull(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    private static void WriteNumberOrNull(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteNumber(name, value.Value);
    }
}

/// <summary>
/// What one daemon reported, already classified. The configuration sections
/// stay as raw JSON text: they are relayed, never read by the Hub.
/// </summary>
public sealed record DaemonViewData(
    string SourceId,
    long ObservedAtMs,
    string State,
    string? ErrorCode,
    DaemonStatus? Status,
    string? ConfigJson,
    string? ConfigUnavailableReason,
    string? DetectionConfigJson,
    string? ScoringConfigJson,
    string? EnergyModel,
    IReadOnlyList<ResultWarning> Warnings);
