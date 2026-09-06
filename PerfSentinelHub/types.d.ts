/**
 * The shapes `wwwroot/launcher.js` annotates against, so `tsc --noEmit` can
 * check the JSDoc that file already carries.
 *
 * Hand-written rather than generated: the JSON the page reads is shaped by the
 * API records, and a generator would either pull in a build step the brief
 * forbids or drift silently. Only what the launcher actually reads is declared,
 * so a field added to a response and never used here does not have to appear.
 */

/** The eight codes an analysis run can fail with. Mirrors `ErrorCodes`. */
export type ErrorCode =
    | "source_unreachable"
    | "source_auth_failed"
    | "source_rejected_request"
    | "timeout"
    | "output_too_large"
    | "binary_failed"
    | "invalid_request"
    | "internal";

/** What a source is read through. Mirrors the `kind` column. */
export type SourceKind = "daemon" | "tempo" | "jaeger_query";

/**
 * The six stored statuses plus the two the page derives. `empty` and `queued`
 * exist for reading only, `analysis_runs.status` never holds them.
 */
export type DisplayStatus =
    | "pending"
    | "running"
    | "succeeded"
    | "failed"
    | "interrupted"
    | "expired"
    | "empty"
    | "queued";

/** How a requested trace count sits against the Hub's caps. */
export type WeightBand = "invalid" | "safe" | "heavy" | "ceiling" | "over";

/** A configured source, as `GET /api/sources` returns it. */
export interface Source {
    id: string;
    name: string;
    environment: string;
    kind: SourceKind;
    retention_hours?: number | null;
    reachable?: boolean;
    last_attempt_ms?: number | null;
    last_success_ms?: number | null;
    unreachable_since_ms?: number | null;
    producer_version?: string | null;
    last_error_code?: ErrorCode | null;
    base_url?: string;
    engine_subcommand?: string | null;
    auth_header_name?: string | null;
    incidents_state?: string | null;
    incidents_read_ms?: number | null;
}

/** One analysis run, as the analyses routes return it. */
export interface Analysis {
    id: string;
    status: Exclude<DisplayStatus, "empty" | "queued">;
    source_id?: string;
    source_name?: string;
    kind?: SourceKind;
    request?: Record<string, unknown> | null;
    result?: { empty?: boolean } & Record<string, unknown> | null;
    error_code?: ErrorCode | null;
    producer_version?: string | null;
    created_ms?: number | null;
    finished_ms?: number | null;
}
