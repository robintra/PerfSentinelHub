namespace PerfSentinelHub.Storage;

internal static class Schema
{
    internal const string V1 = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
          version INTEGER PRIMARY KEY,
          applied_at_ms INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS findings (
          signature TEXT PRIMARY KEY,
          finding_json TEXT NOT NULL,
          service TEXT NOT NULL,
          finding_type TEXT NOT NULL,
          severity TEXT NOT NULL,
          endpoint TEXT NOT NULL,
          template_hash TEXT NOT NULL,
          sample_trace_id TEXT,
          first_seen_ms INTEGER NOT NULL,
          last_seen_ms INTEGER NOT NULL,
          max_confidence TEXT NOT NULL,
          max_confidence_rank INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_findings_service_type_severity_last_seen
          ON findings(service, finding_type, severity, last_seen_ms DESC);
        CREATE INDEX IF NOT EXISTS ix_findings_trace ON findings(sample_trace_id);
        CREATE TABLE IF NOT EXISTS finding_sources (
          signature TEXT NOT NULL REFERENCES findings(signature) ON DELETE CASCADE,
          source_id TEXT NOT NULL,
          source_name TEXT NOT NULL,
          environment TEXT NOT NULL,
          producer_version TEXT NOT NULL,
          first_seen_ms INTEGER NOT NULL,
          last_seen_ms INTEGER NOT NULL,
          PRIMARY KEY(signature, source_id)
        );
        CREATE TABLE IF NOT EXISTS source_state (
          source_id TEXT PRIMARY KEY,
          last_attempt_ms INTEGER NOT NULL,
          last_success_ms INTEGER,
          unreachable_since_ms INTEGER,
          producer_version TEXT,
          last_error_code TEXT
        );
        CREATE TABLE IF NOT EXISTS endpoint_heartbeats (
          source_id TEXT NOT NULL,
          service TEXT NOT NULL,
          endpoint TEXT NOT NULL,
          last_seen_any_ms INTEGER NOT NULL,
          PRIMARY KEY(source_id, service, endpoint)
        );
        """;
}
