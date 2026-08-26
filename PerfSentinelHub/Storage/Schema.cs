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

    // Lineage between a finding and the one it most likely mutated from: same
    // service, detector and endpoint, different template hash. The chain's
    // origin and depth are denormalized onto each row at link time (copied
    // from the predecessor's own row when it has one), so a finding's full
    // lineage survives the retention purge of every earlier hop: the
    // intermediate hops' rows cascade away with their findings, this row
    // does not need them. The heartbeat index serves the status CASE's
    // (service, endpoint) probe, which the PK cannot (it leads with
    // source_id).
    internal const string V2 = """
        CREATE TABLE IF NOT EXISTS finding_lineage (
          successor_signature TEXT NOT NULL REFERENCES findings(signature) ON DELETE CASCADE,
          predecessor_signature TEXT NOT NULL,
          predecessor_first_seen_ms INTEGER NOT NULL,
          origin_first_seen_ms INTEGER NOT NULL,
          depth INTEGER NOT NULL,
          linked_at_ms INTEGER NOT NULL,
          method TEXT NOT NULL,
          PRIMARY KEY(successor_signature, predecessor_signature)
        );
        CREATE INDEX IF NOT EXISTS ix_lineage_predecessor
          ON finding_lineage(predecessor_signature);
        CREATE INDEX IF NOT EXISTS ix_heartbeats_service_endpoint
          ON endpoint_heartbeats(service, endpoint, last_seen_any_ms);
        """;
}
