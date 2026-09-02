# HTTP API

Three surfaces, and they do not overlap. A daemon pushes into the import API. An IDE
plugin or a CI job reads the read API. The browser uses the analysis API and never calls
`/api/findings`.

## Import API

`POST /api/import/findings?source_id=<id>` accepts the daemon's envelope
`{"producer_version":"…","findings":[…]}` with `X-API-Key`. A request carries 1 to 100
findings and at most 2 MiB. The response is sent only after the idempotent signature
upsert commits.

Four imports run at a time, which bounds request memory independently of how many daemons
there are. Writes are serialized against the poll and retention paths. An import that
cannot take the write lock within five seconds gets `503 Retry-After: 1`, and daemon
exporters retain and retry their coalesced batches. Retention purges in bounded chunks, so
a long purge does not reject imports for its whole duration.

A push updates findings and per-source observations only. It never clears the poll path's
`unreachable_since_ms`. A source the Hub cannot reach still reports `unreachable_since`
while its daemon pushes successfully, and that is correct: reachability is a fact about
the Hub's route to the daemon, which a push does not exercise.

## Read API

| Endpoint                             | Returns                                                                                                                                                                                    |
|--------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `GET /api/status`                    | The Hub's version, the engine version it would run (`engine_version`, null when none is configured), and what a run costs: worker count, queue depth, trace cap, timeout, report retention |
| `GET /api/sources`                   | Every configured source with its kind and last known collection state                                                                                                                      |
| `GET /api/findings`                  | Findings, filtered by `service`, `finding_type`, `severity`, `status`, `limit`, `include_acked`                                                                                            |
| `GET /api/findings/{traceId}`        | Findings for a sample trace                                                                                                                                                                |
| `GET /api/sources/{sourceId}/daemon` | One daemon's applied settings and its own account of its state. See below                                                                                                                  |
| `GET /metrics`                       | Prometheus text format, see [OPERATIONS.md](OPERATIONS.md#metrics)                                                                                                                         |
| `GET /health/live`                   | Whether the process is up                                                                                                                                                                  |
| `GET /health/ready`                  | Successful after SQLite initialization                                                                                                                                                     |

On `/api/sources`, timestamps are null for a source never observed, which a reader must
not confuse with the epoch. `producer_version` is null for a trace backend, because a
backend stores traces and detects nothing.

On `/api/findings`, `include_acked` defaults to `true`. Setting it to `false` hides
envelopes carrying a non-null `acknowledged_by`.

### The daemon view

`GET /api/sources/{sourceId}/daemon` reads on demand rather than from the poll. Settings
never change without a restart the Hub has no signal for, and the gauges are the point of
the view.

**Failure is an observation, not a fault.** An unknown source answers `404` and a trace
backend `400`, since it runs no daemon. A daemon that does not answer is reported as
`state: "unreachable"` with an error code, never as a `502`: the Hub relays a source's
health, it does not fail itself.

**What it relays verbatim.** `config` is the daemon's `[daemon]` section. It is null with
`config_unavailable_reason: "api_disabled"` when that daemon serves no query API, which is
a configuration statement rather than a fault. `detection_config`, `scoring_config` and
`energy_model` come from the daemon's export, where `/api/config` does not carry them.
`warnings` is the daemon's own tuning advisor: the Hub relays those sentences and writes
none of its own.

**What it bounds.** A hint past two thousand characters is cut with a visible ellipsis.
Anything past a hundred hints is counted in `warnings_dropped` rather than silently gone.
A failed export read is named in `hints_unavailable_reason` instead of reading as a clean
bill.

**What it derives.** Exactly one thing: `state`, from whether a gauge crossed 90 % of its
cap, the same line the daemon's own monitor draws. It also carries `daemon_defaults`,
`detection_defaults` and `defaults_engine_version`, so a reader can mark what a daemon
actually changed. Those defaults belong to the binary this Hub embeds, so the version is
named rather than assumed and a daemon on another minor is flagged rather than judged.

**How often it reads.** An open row re-reads on the interval its reader picks.
`?refresh=status` makes that tick a single status read instead of the three a full view
takes, because the export is the heavy one and runs at most once a minute. A row whose
read failed re-reads with that same cheap request, and the first one that answers is
followed straight away by a full read, so a row left open recovers by itself.

This polling cannot starve the daemon. The engine's 32-concurrent-request cap is scoped to
its OTLP ingest route precisely so `/api` and `/health` stay responsive, status ticks take
no Hub read slot, and full reads are bounded at two at a time over pooled connections. The
daemon's query surface is HTTP(S) only by design, its gRPC port being OTLP ingest, so the
Hub speaks no RPC to it.

### What the Hub adds to a finding

Each daemon finding is preserved as an opaque, additive JSON document. The Hub adds
`first_seen`, `last_seen`, `max_confidence`, `status`, an optional `lineage`, and source
freshness metadata. IDE clients should ignore unknown fields, as they do with the daemon
API.

`first_seen` comes from the daemon envelope (`first_seen_ms`), clamped to the Hub's
observation time and to a Unix-ms sanity floor. Neither a daemon clock running ahead nor a
seconds-unit bug can distort it, and it falls back to the observation time when a producer
omits the field.

`last_seen` is deliberately the Hub's own observation clock. Retention, ordering and
freshness comparisons rely on it, so it never comes from a remote clock.

### How `status` is derived

Derived at read time, never stored, from data the Hub already keeps:

| Value             | Meaning                                                               |
|-------------------|-----------------------------------------------------------------------|
| `active`          | Seen within `Hub:ResolutionGrace`, 7 days by default                  |
| `likely_resolved` | Gone quiet, but its endpoint still heartbeats from a reachable source |
| `not_observed`    | Nothing proves anything: a silent endpoint, or an unreachable fleet   |

It is a presumption, not a verdict. A finding leaving by retention still leaves silently,
but a reader can now tell "the endpoint runs without the finding" apart from "nobody is
looking". `?status=<value>` filters, and the filter applies before the page limit.

### Lineage

`first_seen` is per signature, so a finding whose normalized template changes gets a new
signature and a new `first_seen`. Since schema v2 the Hub links such a mutation to its
predecessor at import time, when exactly one stored finding:

- shares the service, detector and endpoint,
- has a different template hash,
- was seen within the last 30 days and strictly before the incoming batch,
- and is not itself already superseded.

Ambiguity records nothing, because naming one of several candidates would be a guess.

A linked envelope carries a `lineage` object with `original_first_seen`, the earliest birth
along the chain, and `predecessors`, the chain length. Both are denormalized onto the newest
link, so a finding's full lineage survives the retention purge of every earlier hop. The
heuristic is conservative and non-destructive: the two rows stay separate findings, and the
predecessor ages out through normal retention.

## Analysis API

An analysis is a run of the perf-sentinel binary against one configured source, producing
the self-contained HTML dashboard the engine renders.

| Endpoint                 | Does                                                                          |
|--------------------------|-------------------------------------------------------------------------------|
| `POST /api/analyses`     | Takes `{"source_id": "...", "request": {...}}`, answers `202` with the run id |
| `GET /api/analyses`      | Lists recent runs, newest first                                               |
| `GET /api/analyses/{id}` | Returns one run                                                               |
| `GET /reports/{id}.html` | Serves a succeeded run's report, from the same origin as the rest             |

The request shape follows the source's kind: `{}` for a daemon, which takes no parameters
at all, `{service, lookback | from_ms + to_ms, max_traces}` for a trace backend, or
`{trace_id}`. The engine's own exclusions are enforced before anything is queued, so an
impossible pair is refused rather than discovered as a failed run three minutes later.

### Two invocations, not one

The query subcommands emit text, JSON or SARIF, and only `report` writes HTML. So the
source is read into a report JSON, and that JSON is then rendered. A daemon source skips
the first step, since its own `/api/export/report` already returns one.

Both invocations run from `Hub:Analysis:ReportDirectory`. The engine looks for
`.perf-sentinel.toml` relative to its own working directory, so leaving this unset would
let a stray file beside whatever directory launched the Hub decide detection thresholds
for every run.

### Detection overrides

A request may carry a `detection` object overriding the engine's thresholds:
`n_plus_one_min_occurrences`, `window_duration_ms`, `slow_query_threshold_ms`,
`slow_query_min_occurrences`, `max_fanout`, `chatty_service_min_calls`,
`pool_saturation_concurrent_threshold`, `serialized_min_sequential`, and, from engine
0.18.0, `sanitizer_aware_classification` (one of `auto`, `strict`, `always`, `never`) and
`sanitizer_aware_min_cv` (a decimal from `0.01` to `10`).

Bounds mirror the engine's own validator, and `GET /api/status` publishes them with each
default under `detection_knobs`. Each entry names its `kind`: `integer` and `decimal`
carry `min`, `max` and a numeric `default`, `choice` carries its `choices` and a string
`default`. A knob the probed engine's `[detection]` does not read is withheld from the list
rather than offered and refused at run time, so the two sanitizer knobs appear only once
the embedded binary is 0.18.0 or later. A value equal to the default is dropped rather than
recorded, so a run carries only what departs from the standard configuration. The
overrides are written to a per-run TOML handed to both invocations through `-c`, and
deleted when the run ends.

These thresholds decide what counts as a problem, not how the report is written. Raising
one does not make a run lighter, it stops the detector from reporting the smaller cases.
That is why counts from runs with different thresholds are not comparable, and why the
launcher says so. A daemon source takes none: it detects with its own configuration, and
the Hub only reads what it already found.

### Report size

Not a knob. The sink's 5 MiB target is a private constant with no flag, no environment
variable and no config key. A report built from a backend query tops out around 4 MB,
because the share of that budget reserved for embedded span trees is never spent: a
backend query returns findings, not spans. When the sink does drop findings to fit, the
run records how many survived, read back from the rendered file, and the result panel says
so above the link.

### Failure and expiry

Every failure is one of eight codes: `source_unreachable`, `source_auth_failed`,
`source_rejected_request`, `timeout`, `output_too_large`, `binary_failed`,
`invalid_request`, `internal`.

Raw stderr never leaves the process. It is read to name an owner, since "the backend
refused us" and "the binary broke" have different owners, and that classification is a
heuristic on a bounded set of markers rather than a contract.

Reports are deleted `Hub:Analysis:ReportRetention` after they succeed, and the run is
marked expired while keeping its parameters. The row itself then survives until
`Hub:Analysis:RunRetention`, thirty days by default, after which it is deleted and
`GET /api/analyses/{id}` answers `404`. This is not an audit trail, and a link shared
yesterday is already dead. A run still pending or running is never removed, however old
its row looks.

A run still running when the service stops comes back `interrupted` and is never replayed
on its own. A silent retry would fire a second heavy query at a backend nobody asked to
query twice.
