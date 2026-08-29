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
        JsonWrite.StringOrNull(writer, "error_code", view.ErrorCode);
        JsonWrite.StringOrNull(writer, "version", view.Status?.Version);
        JsonWrite.NumberOrNull(writer, "uptime_seconds", view.Status?.UptimeSeconds);

        WriteRawOrNull(writer, "config", view.ConfigJson);
        JsonWrite.StringOrNull(writer, "config_unavailable_reason", view.ConfigUnavailableReason);
        WriteRawOrNull(writer, "detection_config", view.DetectionConfigJson);
        // The defaults ride along so a reader can mark what was changed, and
        // the version they belong to rides with them: a daemon on another
        // minor may have a different default, and the view says so instead of
        // calling a value wrong.
        WriteRawOrNull(writer, "daemon_defaults", DaemonDefaults.DaemonJson);
        WriteRawOrNull(writer, "detection_defaults", DaemonDefaults.DetectionJson);
        writer.WriteString("defaults_engine_version", view.DefaultsEngineVersion);
        WriteRawOrNull(writer, "scoring_config", view.ScoringConfigJson);
        JsonWrite.StringOrNull(writer, "energy_model", view.EnergyModel);

        var gauges = view.Status is null ? null : DaemonView.Read(view.Status);
        WriteGauge(writer, "traces", gauges?.Traces);
        WriteGauge(writer, "analysis_queue", gauges?.AnalysisQueue);
        WriteGauge(writer, "findings", gauges?.Findings);

        JsonWrite.StringOrNull(writer, "hints_unavailable_reason", view.HintsUnavailableReason);
        writer.WriteNumber("warnings_dropped", view.WarningsDropped);
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
        JsonWrite.NumberOrNull(writer, "value", gauge.Value);
        JsonWrite.NumberOrNull(writer, "capacity", gauge.Capacity);
        if (gauge.Pct is { } pct)
            writer.WriteNumber("pct", Math.Round(pct, 1));
        else
            writer.WriteNull("pct");
        writer.WriteBoolean("at_capacity", gauge.AtCapacity);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Every string that reaches this method was already validated by a parse
    /// upstream: the endpoint's shape checks for the config, GetRawText of a
    /// parsed element for the sections, and the defaults a test pins. So it is
    /// written with the writer's own single validation pass and no second
    /// document, which is what a 5-second refresh cadence asks for.
    /// </summary>
    private static void WriteRawOrNull(Utf8JsonWriter writer, string name, string? json)
    {
        if (json is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WritePropertyName(name);
        writer.WriteRawValue(json, skipInputValidation: false);
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
    // The engine version whose defaults are published alongside, which is the
    // binary this Hub embeds and not necessarily the one the daemon runs.
    string DefaultsEngineVersion,
    // Null when the export snapshot was read; otherwise the error code of the
    // read that failed, because unread hints are not the same thing as none.
    string? HintsUnavailableReason,
    IReadOnlyList<ResultWarning> Warnings,
    int WarningsDropped);
