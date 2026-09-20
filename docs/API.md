# HTTP API

Four surfaces, and they do not overlap. A daemon pushes into the import API. An IDE
plugin or a CI job reads the read API. The browser uses the analysis API and the ack relay,
reads `/api/incidents` for its incidents screen, and never calls `/api/findings`.

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
| `GET /api/findings`                  | Findings, filtered by `service`, `finding_type`, `severity`, `status`, `signature`, `environment`, `source_id`, `from`, `to`, `offset`, `limit`, `include_acked`                           |
| `GET /api/findings/{traceId}`        | Findings for a sample trace                                                                                                                                                                |
| `GET /api/sources/{sourceId}/daemon` | One daemon's applied settings and its own account of its state. See below                                                                                                                  |
| `GET /api/incidents`                 | The incidents the polled daemons recorded, newest first, filtered by `service`, `kind`, `namespace`, `environment`, `source_id`, `offset`, `limit`. Without their findings, see below      |
| `GET /api/incidents/{id}`            | One incident whole, the frozen findings included                                                                                                                                           |
| `POST /api/incidents/refresh`        | Reads every daemon's incidents ring now, then answers exactly as `GET /api/incidents`. Same parameters. See below                                                                          |
| `GET /metrics`                       | Prometheus text format, see [OPERATIONS.md](OPERATIONS.md#metrics)                                                                                                                         |
| `GET /health/live`                   | Whether the process is up                                                                                                                                                                  |
| `GET /health/ready`                  | Successful after SQLite initialization                                                                                                                                                     |

On `/api/sources`, timestamps are null for a source never observed, which a reader must
not confuse with the epoch. `producer_version` is null for a trace backend, because a
backend stores traces and detects nothing.

`acks_state` and `acks_read_ms` say what the last read of a daemon's acknowledgments came to
and when it was taken, both null when none has run. The states are those of
`incidents_state`, where `absent` also covers a daemon below 0.24.0, which is never asked,
plus `truncated`: the listing reached the daemon's cap of a thousand acks, so its tail may
be missing.

`ack_relay` is true when the Hub holds a credential to write acks to that daemon, see
[CONFIGURATION.md](CONFIGURATION.md#the-ack-credential). Neither its header nor its value is
ever published.

On `/api/findings`, `include_acked` defaults to `true`. Set to `false`, it lists a finding
only while at least one source in scope holds it un-acknowledged, every source counting when
the read has no scope, see
[How `include_acked` judges a finding](#how-include_acked-judges-a-finding). `signature` is
an exact match. The Hub bounds it and leaves its shape to the daemon: past 1,024 characters,
or with a control character, it is a `400`. A `service`, `finding_type`, `severity`,
`status` or `signature` given empty or as whitespace only reads as absent, which is what a
dashboard sends for its "All" choice. The rows come in a total order, `last_seen` descending
then `signature`, and `offset` skips that many of them before `limit` applies. `offset` runs
from 0 to 1,000,000, and a value outside that range is a `400` rather than a clamped page.

`environment` and `source_id` scope the read, to the sources of one environment or to one
source. Both are closed sets, the Hub's configured sources, and a value outside them is a
`400` rather than an empty page, because a typo must not read as "no findings". Given empty
or as whitespace only, either reads as absent like the filters above, so it is neither a
`400` nor an empty page. `environment` resolves to every source configured with it. Given
together with `source_id` the two intersect, so a source outside the named environment
lists nothing, the answer a pair of filters that exclude each other has. Without either, the
read covers the whole fleet as it always has. A scoped answer describes its scope rather
than the fleet, see [What the Hub adds to a finding](#what-the-hub-adds-to-a-finding).

`from` and `to` bound the read to an observation window, in epoch milliseconds, each
optional and both inclusive. A finding is listed when a source in scope observed it inside
the window, so the window honours `environment` and `source_id`. It selects rows and does
not rewrite them: `first_seen`, `last_seen` and `status` still describe the finding as it
stands now. A value that is not a plain non-negative integer, a sign included, is a `400`,
and so is a `from` greater than `to`. Given empty or as whitespace only, either reads as
absent and leaves its side open.

The window has the granularity of a day. The Hub records the days each source observed a
finding, whole UTC days on its own clock, so a bound that falls inside a day takes that
whole day. Before the first day the Hub recorded for a source, that source is assumed to
have carried the finding from its `first_seen` to the start of that day, and up to its
`last_seen` when no day was ever recorded, which is the case of a database written before
the record existed. [LIMITATIONS.md](LIMITATIONS.md) says what that assumption costs.

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

### Incidents

A perf-sentinel daemon from 0.20.0 with `[daemon.incidents]` enabled records an incident
when the operator's alerting posts one, and freezes the findings of the minutes before
it. The Hub copies that record on every poll of the daemon and re-derives nothing: the
findings inside an incident are the daemon's, frozen at capture and settled once, and the
Hub's own finding of the same signature may since have moved on. The copy outlives the
daemon's ring, which dies with the daemon. A copy is one daemon's capture, keyed on the
incident's `id` and the `source_id` together: the id hashes the service, the kind and the
alerting's stamp and nothing of the daemon, so two daemons fed the same alert list the same
id and each keeps its own frozen findings. Of two captures of the same incident by one
daemon the richer one is kept, since Alertmanager repeats a firing alert and a daemon
restarted in between freezes a window its ring no longer reaches. Copies expire with the
findings retention, on the Hub's own clock and never on the alerting's `at_ms`.

The poll reads the daemon's ring a page at a time under a 4 MiB body cap. A page that
overflows it is re-read at half the size from the same offset, down to a single incident,
since the daemon embeds up to a thousand findings per incident and a full page of a busy
daemon never fits while one incident always does. An incident that overflows the cap on
its own is filed as `response_too_large`, and a page that is not a JSON array as
`invalid_incidents`.

The poll reads the route with the source's `AuthHeaderName` and `AuthHeaderValue`, so a
read key is `AuthHeaderName=X-API-Key` with the daemon's `[daemon] read_api_key` as the
value. What the read came to is `incidents_state` on `/api/sources`: `ok`, `absent` (a
daemon before 0.20.0 answers 404 and one with the store disabled 503, neither a failure),
`unauthorized`, `error`, or null when no poll has run yet. None of these touches the
source's reachability: the findings were collected on the same poll, and a refused key
must not demote every finding that daemon reported.

`GET /api/incidents` lists the copies newest first, one row per `(id, source_id)`, without
their findings, which can run to a thousand per incident and are never read for the
listing. Each carries the daemon's own fields plus `source_id`, `source_name`,
`environment`, `first_seen`, `last_seen` (Hub clock), `finding_count` and `capture`:
`complete` when `oldest_finding_ms` is at or below `window_from_ms`, `partial` when the
ring had already evicted part of the window, `empty` when it held nothing. Among the
daemon's fields, `namespace` is the alert label a 0.20.0 daemon carries when the alert
named one, relayed as the daemon wrote it and absent when it did not: a tag for reading
and filtering, never a key.

The listing filters on `service`, `kind`, `namespace`, `environment` and `source_id`, and
pages with `offset` and `limit`. `service` and `namespace` are free strings matched
exactly, and an unknown one is an empty page. `kind`, `environment` and `source_id` are
closed sets, the daemon's five kinds and the Hub's configured sources, and a value outside
them is a `400` rather than an empty page, because a typo must not read as "no incidents".
Any of these five filters given empty or as whitespace only reads as absent, closed sets
included, so it is neither a `400` nor an empty page. `environment` resolves to every
source configured with it. Given together with `source_id` the two intersect, so a source
outside the named environment lists nothing, the answer a pair of filters that exclude
each other has.

`GET /api/incidents/{id}` returns one incident whole, findings included, the richest copy
when several sources hold the id. A finding whose `first_seen_ms` is past `at_ms` fired
only after the restart. Like `/api/findings`, both
answer whoever reaches the Hub's port: a daemon's read key protects the daemon, and the
Hub re-exposes the frozen findings behind whatever fronts the Hub.

`POST /api/incidents/refresh` reads the fleet on demand, the way the daemon view does, and
the incidents screen calls it every time it is opened. The poll is the floor rather than
the only path: an operator paged about an OOM kill opens the screen within the minute and
`Hub:PollInterval` is an hour, so a poll-only screen would show nothing until the next one.
A POST because it writes to the store, and because a GET would be cached and prefetched,
which a fleet read must never be. It fans out over every source of kind `daemon`, bounded
by `Hub:MaxConcurrentPolls` exactly as the poll worker is, and answers the same body as
`GET /api/incidents` with the same parameters, validated identically, so a screen needs
one round trip. The filters narrow the answer, never the read: the fan-out covers the
whole fleet whatever the query asks for. It shares the poll's failure isolation: a
401, a 404, a 503 or an oversized page files its own `incidents_state` and never touches
the source's reachability, and one refusing daemon costs the others nothing.

What it guarantees the daemons: a source whose last read is younger than ten seconds is
skipped and its stored copy served instead, so a reload loop, or five people watching the
same screen, cannot storm the fleet. That floor is a constant and not a setting, because it
protects the daemons from this Hub rather than expressing a preference. A second guarantee
sits in front of it, a gate of two concurrent refreshes, which answers `503` with
`Retry-After: 1` past that, since each refresh buffers a page per daemon under the 4 MiB
body cap. `incidents_read_ms` on `/api/sources` carries when each copy was taken, null
alongside a null `incidents_state` when none ever was: without it a quiet fleet and a stale
copy read the same.

### What the Hub adds to a finding

Each daemon finding is preserved as an opaque, additive JSON document. The Hub adds
`first_seen`, `last_seen`, `max_confidence`, `status`, an optional `lineage`, `sources`, one
entry per source that reported the finding, and an optional `acks`. An entry of `sources`
carries the source's `id`, the one `/api/sources` lists and `source_id` filters on, its
`name`, `environment` and `producer_version`, and how fresh its observation is. IDE clients
should ignore unknown fields, as they do with the daemon API.

`acks` lists the active acknowledgments the Hub mirrored from the daemons, one entry per
source of `sources` whose daemon holds one on this signature, in the same order. It is
absent when no source holds any, so a finding nobody acknowledged reads as it always has. An
entry carries the `source_id`, the `source` of the ack, `daemon` for one taken at runtime
and `toml` for one of the CI baseline, then `by`, `reason` when the daemon gave one, `at`,
and `expires_at` when the ack expires. `at` and `expires_at` are the daemon's text, relayed
as it came. An ack is active while it has no expiry or its expiry is still ahead on the
Hub's clock, and only a source whose last ack read came to `ok` or `truncated` is listed,
since the rows a failed read leaves behind prove nothing. The name is reserved like the
Hub's other fields: an `acks` property a daemon sends is dropped, and the daemon's own
`acknowledged_by` is relayed verbatim beside the Hub's list.

`first_seen` comes from the daemon envelope (`first_seen_ms`), clamped to the Hub's
observation time and to a Unix-ms sanity floor. Neither a daemon clock running ahead nor a
seconds-unit bug can distort it, and it falls back to the observation time when a producer
omits the field.

`last_seen` is deliberately the Hub's own observation clock. Retention, ordering and
freshness comparisons rely on it, so it never comes from a remote clock.

Read with `environment` or `source_id`, an envelope describes that scope and not the fleet.
The daemon's document is the copy of the source in scope that saw the finding last, so
`severity` judges that copy. `first_seen` is the earliest and `last_seen` the latest over
the sources in scope, `status` is derived from that `last_seen` and from the heartbeats of
those sources alone, and `sources` and `acks` list only them. `max_confidence` stays
fleet-wide on purpose, the highest confidence any source ever reported. A source's own copy
starts at its first observation after the upgrade that records it. Until then its row
serves the copy the fleet shares, the freshest one from any source.

### How `include_acked` judges a finding

`include_acked=false` judges each source in scope that carries the finding, by whichever of
its two views the Hub read last. When the last read of that source's acks came to `ok` and
is no older than the source's last observation of the finding, the mirror decides: the
finding is acknowledged there when the mirror holds an active ack on its signature.
Otherwise the source's own copy of the envelope decides, by a non-null `acknowledged_by`,
which is all the Hub judged before it mirrored acks. A source whose acks were never read, a
failed read and a `truncated` listing all fall to the envelope, and a source with no copy of
its own yet is judged on the copy the fleet shares, in a scoped read too. The finding is
listed while at least one such source holds it un-acknowledged, so a finding acknowledged in
production stays listed for staging, and for the fleet that includes staging. The filter
applies before the page limit.

### How `status` is derived

Derived at read time, never stored, from data the Hub already keeps:

| Value             | Meaning                                                               |
|-------------------|-----------------------------------------------------------------------|
| `active`          | Seen within `Hub:ResolutionGrace`, 7 days by default                  |
| `likely_resolved` | Gone quiet, but its endpoint still heartbeats from a reachable source |
| `not_observed`    | Nothing proves anything: a silent endpoint, or an unreachable fleet   |

It is a presumption, not a verdict. A finding leaving by retention still leaves silently,
but a reader can now tell "the endpoint runs without the finding" apart from "nobody is
looking". `?status=<value>` filters, and the filter applies before the page limit. In a
scoped read the status is the scope's own: a finding production still reports can be
`not_observed` for staging, and only a heartbeat from a source in scope makes it
`likely_resolved` there.

### Lineage

`first_seen` is per signature, so a finding whose normalized template changes gets a new
signature and a new `first_seen`. Since schema v2 the Hub links such a mutation to its
predecessor at import time, when exactly one stored finding:

- shares the service, detector and endpoint,
- has a different template hash,
- was seen within the last 30 days and strictly before the incoming batch,
- and is not itself already superseded.

A signature also changes when the **endpoint** changes, and that path is not linked. The
rule above holds the endpoint fixed and looks for a moved template, so the reverse, a
stable template on a renamed endpoint, finds no candidate and the successor carries no
`lineage` block. An engine upgrade that teaches the daemon to resolve an endpoint it used
to report as `unknown` is the case that produces this.

Ambiguity records nothing, because naming one of several candidates would be a guess.

A linked envelope carries a `lineage` object with `original_first_seen`, the earliest birth
along the chain, and `predecessors`, the chain length. Both are denormalized onto the newest
link, so a finding's full lineage survives the retention purge of every earlier hop. The
heuristic is conservative and non-destructive: the two rows stay separate findings, and the
predecessor ages out through normal retention.

## Ack relay

Two routes write at the daemon behind one source, with that daemon's own ack key. Both are
a `POST` with a JSON body, and neither is ever open: under `Hub:Auth` they need a session.

| Endpoint                                   | Body                                              | Does                                    |
|--------------------------------------------|---------------------------------------------------|-----------------------------------------|
| `POST /api/sources/{sourceId}/acks`        | `{"signature":"…","reason":"…","expires_at":"…"}` | Acknowledges the finding at that daemon |
| `POST /api/sources/{sourceId}/acks/revoke` | `{"signature":"…"}`                               | Revokes the ack that daemon holds       |

`expires_at` is optional, and an ack without one is permanent. A `by` in the body is
ignored: the Hub names the caller itself.

The Hub judges a request in this order, and a request it refuses never reaches the daemon:

1. **The caller.** The signed-in user under `Hub:Auth`. Otherwise the value of
   `Hub:Analysis:IdentityHeader`, and only when `Hub:AckRelay:TrustIdentityHeader` is set,
   see [AUTHENTICATION.md](AUTHENTICATION.md#how-it-works). With nobody to name, a `403`.
   It comes first so that an unnamed caller learns nothing about the sources.
2. **The source.** An unknown id, a trace backend and a daemon with no ack credential all
   answer the same `404`. `ack_relay` on `/api/sources` says which sources relay.
3. **Where the request comes from.** A `Sec-Fetch-Site` header that is present and is not
   `same-origin` is a `403`, and a content type other than `application/json` is a `415`.
   The Hub has no antiforgery token and no CORS policy, so these two are its defence
   against a page on another origin: a browser sets the first by itself, and the second
   cannot be sent across origins without a preflight nothing here answers.
4. **Room.** Two relays run at a time, and one more gets `503` with `Retry-After: 1`. A
   body over 8 KiB is a `413`.
5. **The body.** `signature` is required, at most 1,024 characters, free of control
   characters, and neither `.` nor `..`, which would leave the daemon's ack path. Its shape
   stays the daemon's rule. `reason` is required on an ack, not blank, and held to the same
   bounds. `expires_at` is an RFC 3339 time in the future. Anything else is a `400`.
6. **The pair.** The Hub must hold that finding at that source. A revoke passes on that
   too, and also on an ack of it the Hub mirrors from that source, which can outlive the
   finding. Otherwise a `404`. A daemon acknowledges any canonical signature, so this is
   what bounds the relay to findings the Hub knows.

The Hub then writes the daemon's request itself, and nothing the caller sent travels as
bytes. It is a `POST`, or a `DELETE` for a revoke, on `api/findings/{signature}/ack`, the
signature percent-encoded so that one holding a slash stays one path segment. It carries the
source's ack credential and never the read one. The body of an ack is
`{"by":"…","reason":"…","expires_at":"…"}`, `expires_at` written back in UTC to the second.
The caller travels as `by` on an ack and as `X-User-Id` on a revoke, unchanged when it is
printable ASCII with no space at either end, percent-encoded otherwise, and in the same
form on both so the two compare equal at the daemon. The whole exchange has three times
`Hub:HttpTimeout`.

| The daemon answers | The Hub answers                                                             |
|--------------------|-----------------------------------------------------------------------------|
| any `2xx`          | `204`                                                                       |
| `400`              | `400`, the daemon refused the signature as not canonical                    |
| `401`              | `502`, the daemon refused the Hub's ack credential                          |
| `404` on a revoke  | `404`, not acked at this daemon, or only by its CI baseline                 |
| `409`              | `409`, already acked at the daemon or by its CI baseline                    |
| `503`              | `503`, acknowledgments are disabled on this daemon                          |
| `507`              | `507`, the daemon's ack store is full                                       |
| nothing in time    | `504`                                                                       |
| anything else      | `502`, a `404` on an ack included, which means the daemon has no such route |

A `401` from the daemon is never relayed as one. It says the key the Hub holds is wrong,
which no sign-in mends, and the launcher answers a `401` by reloading into the sign-in.
Every refusal the Hub words itself carries `{"detail":"…"}`. The `413`, the `415` and the
`503` of a full gate have no body.

After a `2xx` the Hub reads that daemon's ack listing again, so `acks[]` and
`include_acked=false` follow at once instead of at the next poll. It does so for a source
the poll has reached, since the read is decided by the daemon's version, and a failure of
that read never turns the `204` into an error: the daemon took the write. Each relay logs
one line, event `1310` when the daemon answered and `1311` when it did not, with the
source, the action, the caller and the status. The reason and the credential are never
logged.

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
`pool_saturation_concurrent_threshold`, `serialized_min_sequential`,
`sanitizer_aware_classification` (one of `auto`, `strict`, `always`, `never`) and, from
engine 0.18.0, `sanitizer_aware_min_cv` (a decimal from `0.01` to `10`).

Bounds mirror the engine's own validator, and `GET /api/status` publishes them with each
default under `detection_knobs`. Each entry names its `kind`: `integer` and `decimal`
carry `min`, `max` and a numeric `default`, `choice` carries its `choices` and a string
`default`. A knob the probed engine's `[detection]` does not read is withheld from the list
and refused on submission with a 400 that names both versions, rather than written to the
run config and refused by the engine at run time, so `sanitizer_aware_min_cv` appears only
once the embedded binary is 0.18.0 or later. A value equal to the default is dropped rather than
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
