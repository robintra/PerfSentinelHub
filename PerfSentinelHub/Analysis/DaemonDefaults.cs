namespace PerfSentinelHub.Analysis;

/// <summary>
/// The engine's own defaults, so a daemon view can say which settings were
/// actually changed. Held as literal JSON and relayed rather than modelled,
/// for the same reason the sections beside them are: the Hub reads none of
/// these values, it only hands them to a reader that compares.
///
/// These are the defaults of the engine version this Hub embeds, exactly as
/// the engine's own `query monitor` compares against the defaults compiled
/// into the binary running it. A daemon on a different minor can therefore
/// have a different default, which is why the view names the version it
/// compared against instead of asserting a value is wrong.
///
/// Transcribed from `impl Default for DaemonConfig` and `for DetectConfig` in
/// sentinel-core. The detection keys are DetectConfig's, which the export
/// serialises, not the file keys the launcher's knobs carry.
/// </summary>
internal static class DaemonDefaults
{
    public const string DaemonJson = """
        {
          "listen_addr": "127.0.0.1",
          "listen_port": 4318,
          "listen_port_grpc": 4317,
          "json_socket": "/tmp/perf-sentinel.sock",
          "max_active_traces": 10000,
          "trace_ttl_ms": 30000,
          "sampling_rate": 1.0,
          "max_events_per_trace": 1000,
          "max_payload_size": 16777216,
          "environment": "staging",
          "max_retained_findings": 10000,
          "max_export_findings": 1000,
          "max_retained_traces": 50,
          "ingest_queue_capacity": 1024,
          "analysis_queue_capacity": 1024,
          "memory_high_water_pct": 0,
          "api_enabled": true,
          "tls_configured": false,
          "ack_enabled": true,
          "ack_api_key_set": false,
          "cors_allowed_origins": [],
          "archive_configured": false,
          "correlation_enabled": false,
          "correlation_window_ms": 600000,
          "correlation_lag_threshold_ms": 5000,
          "correlation_min_co_occurrences": 5,
          "correlation_min_confidence": 0.7,
          "correlation_max_tracked_pairs": 10000
        }
        """;

    public const string DetectionJson = """
        {
          "n_plus_one_threshold": 5,
          "window_ms": 500,
          "slow_threshold_ms": 500,
          "slow_min_occurrences": 3,
          "max_fanout": 20,
          "chatty_service_min_calls": 15,
          "pool_saturation_concurrent_threshold": 10,
          "serialized_min_sequential": 3,
          "sanitizer_aware_classification": "auto"
        }
        """;
}
