<p align="center">
    <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fraw.githubusercontent.com%2Frobintra%2FPerfSentinelHub%2Fmain%2Fglobal.json&query=%24.sdk.version&label=.NET&color=512BD4&logo=dotnet&logoColor=white" alt=".NET" /></a>
    <a href="https://github.com/robintra/PerfSentinelHub/actions/workflows/ci.yml"><img src="https://github.com/robintra/PerfSentinelHub/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
    <a href="https://github.com/robintra/PerfSentinelHub/actions/workflows/security-audit.yml"><img src="https://github.com/robintra/PerfSentinelHub/actions/workflows/security-audit.yml/badge.svg" alt="Security Audit" /></a>
    <a href="https://github.com/robintra/PerfSentinelHub/actions/workflows/codeql.yml"><img src="https://github.com/robintra/PerfSentinelHub/actions/workflows/codeql.yml/badge.svg" alt="CodeQL" /></a>
    <a href="https://sonarcloud.io/summary/overall?id=robintrassard_PerfSentinelHub"><img src="https://sonarcloud.io/api/project_badges/measure?project=robintrassard_PerfSentinelHub&metric=coverage" alt="Coverage" /></a>
    <a href="https://sonarcloud.io/summary/overall?id=robintrassard_PerfSentinelHub"><img src="https://sonarcloud.io/api/project_badges/measure?project=robintrassard_PerfSentinelHub&metric=alert_status" alt="Quality Gate" /></a>
    <a href="https://github.com/robintra/PerfSentinelHub/actions/workflows/release.yml"><img src="https://github.com/robintra/PerfSentinelHub/actions/workflows/release.yml/badge.svg" alt="Release" /></a>
</p>

# PerfSentinelHub

PerfSentinelHub gives IDE plugins one durable endpoint for findings collected from one or more
[perf-sentinel](https://github.com/robintra/perf-sentinel) instances.
It is a NativeAOT service backed by SQLite: daemon push is the primary path, hourly polling is a
recovery safety net, and the Hub preserves read-compatible finding envelopes for 180 days by default.

Every badge above reports something observed, except the release one, which stays empty until the
release workflow runs for the first time. Badges for the container image and the Helm chart are
deliberately absent: their package pages answer 404 until a release publishes them, and a badge
that links nowhere is a promise rather than evidence. They come back with the first release.

## Release contract and maturity

The configured release contract accepts only stable `v0.x.y` tags. “Stable” means a release has no
prerelease suffix or beta channel; `0.x.y` still denotes pre-1.0 maturity, so compatibility may
change between minor versions. The configured first release is `v0.1.0`; publication is not
claimed until the linked release destination and public verification workflow show it.

Each release is closed to these four NativeAOT runtime targets and their matching symbol archives:

- `linux-x64`
- `linux-arm64`
- `osx-arm64`
- `win-x64`

There is no macOS AMD64 or Windows ARM64 artifact. The same closed release also contains one
multi-architecture Linux OCI image archive, one digest-bound Helm chart, an SPDX document and a
Cosign bundle for every subject, plus GitHub provenance. `release-manifest.json` and `SHA256SUMS`
bind the exact filenames, source commit, and digests.

## Run locally in five minutes

Requirements: .NET SDK 10.0.400 and a reachable perf-sentinel daemon.

```bash
Hub__DatabasePath=/tmp/perf-sentinel-hub.db \
Hub__Sources__0__Id=local \
Hub__Sources__0__Name='Local daemon' \
Hub__Sources__0__Environment=development \
Hub__Sources__0__BaseUrl=http://localhost:4318 \
Hub__Sources__0__ImportApiKey="$(openssl rand -hex 16)" \
ASPNETCORE_URLS=http://localhost:5080 \
dotnet run --project PerfSentinelHub

curl http://localhost:5080/health/ready
curl http://localhost:5080/api/findings
```

The first poll starts immediately. The SQLite file survives restarts at the configured path.

## Install with Helm from source

For local evaluation, the source chart always deploys one replica and a persistent volume. Supply
at least one source and an immutable image digest:

```bash
helm upgrade --install perf-sentinel-hub deploy/helm/perf-sentinel-hub \
  --set image.repository=ghcr.io/robintra/perf-sentinel-hub \
  --set image.digest=sha256:IMAGE_DIGEST \
  --set 'sources[0].id=production' \
  --set 'sources[0].name=Production' \
  --set 'sources[0].environment=production' \
  --set 'sources[0].baseUrl=http://perf-sentinel.observability:4318' \
  --set persistence.size=5Gi
```

For an authenticated daemon poll, put the value in a Kubernetes Secret, then set
`sources[].authHeaderName`, `sources[].authSecretName`, and `sources[].authSecretKey`. Never put
the credential itself in Helm values. For daemon push, set `sources[].importSecretName` and
`sources[].importSecretKey`; the referenced value must contain at least 32 characters.

For a public release, use the image digest recorded in its authenticated release manifest. Resolve
the chart's registry digest once, record it, and pull or install only `oci://...@sha256:...`; a
version tag is a discovery hint, never a deployment identity:

```bash
IMAGE_DIGEST="$(jq -r .image.digest release/release-manifest.json)"
docker pull "ghcr.io/robintra/perf-sentinel-hub@$IMAGE_DIGEST"

CHART=ghcr.io/robintra/charts/perf-sentinel-hub
CHART_DIGEST="$(oras resolve "$CHART:0.1.0")"
helm pull "oci://$CHART@$CHART_DIGEST"
```

These registry commands are usable only after the public rehearsal and publication succeed.

## Configuration

Environment variables use the .NET `Hub__...` form; Helm exposes the same settings under `hub`
and `sources`.

| Setting | Default | Validation |
| --- | --- | --- |
| `Hub:DatabasePath` | `/data/hub.db` | Absolute path |
| `Hub:PollInterval` | `01:00:00` | Positive duration |
| `Hub:HttpTimeout` | `00:00:10` | Positive duration |
| `Hub:MaxConcurrentPolls` | `4` | 1–32 |
| `Hub:Retention` | `180.00:00:00` (180 days) | Positive duration |
| `Hub:ResolutionGrace` | `7.00:00:00` (7 days) | Positive, below `Retention` |
| `Hub:DefaultReadLimit` | `1000` | 1–`MaxReadLimit` |
| `Hub:MaxReadLimit` | `10000` | 1–10000 |
| `Hub:Analysis:EngineBinaryPath` | none | Optional, absolute path to the perf-sentinel binary. Absent means analysis runs are unavailable |
| `Hub:Analysis:ReportDirectory` | `/data/reports` | Absolute, writable. Rendered reports live here |
| `Hub:Analysis:IdentityHeader` | `X-Forwarded-User` | Header a reverse proxy sets with the requester's identity |
| `Hub:Analysis:Workers` | `2` | 1–16 |
| `Hub:Analysis:MaxTracesCap` | `2000` | 1–100000 |
| `Hub:Analysis:Timeout` | `00:05:00` | Positive, at most one hour |
| `Hub:Analysis:ReportRetention` | `1.00:00:00` (24 hours) | Positive duration |
| `Hub:Sources` | none | At least one source |
| `Sources[].Id` | none | Non-empty and unique |
| `Sources[].Name` | none | Non-empty |
| `Sources[].Environment` | none | Non-empty |
| `Sources[].Kind` | `daemon` | One of `daemon`, `tempo`, `jaeger_query`. Only a daemon is polled and only a daemon may carry an import key |
| `Sources[].RetentionHours` | none | Trace backends only, 1 hour to 10 years. How far back this backend keeps traces, declared because no backend API exposes it |
| `Sources[].BaseUrl` | none | Required; absolute HTTP(S) URL without credentials, query, or fragment. A path prefix is kept, so `https://gw/perf-sentinel/` polls `https://gw/perf-sentinel/api/status` |
| `Sources[].AuthHeaderName/Value` | none | Both absent or both present; no newlines |
| `Sources[].ImportApiKey` | none | Optional push credential; at least 32 characters, supplied through a Secret |

### An https source with a private CA

The Hub validates a source's certificate against the container's trust store, so
an in-cluster daemon with a self-signed or internally-issued certificate is refused
with `PartialChain` until its CA is in there. The runtime image is chiseled and has
no shell, so `update-ca-certificates` cannot be run in it. Point `SSL_CERT_FILE` at a
bundle instead, mounted from a ConfigMap:

```yaml
env:
  - name: SSL_CERT_FILE
    value: /etc/perf-sentinel-hub/certs/bundle.crt
```

The bundle has to be the public roots and your CA concatenated, in that order, not
the CA alone: the variable replaces the default file rather than adding to it, and a
Hub that only trusts your CA cannot reach anything on the public internet. Verified
against the runtime this image is built on: with no variable the private certificate
is refused and public TLS works, with the concatenated bundle both work.

## Import API

`POST /api/import/findings?source_id=<id>` accepts the daemon's JSON envelope
`{"producer_version":"…","findings":[…]}` with `X-API-Key`. A request contains 1–100 findings
and at most 2 MiB. The response is sent only after the idempotent signature upsert commits.

The Hub admits four imports at a time, which bounds request memory independently of the number of
daemons; writes themselves are serialized against the poll and retention paths. An import that
cannot take the write lock within five seconds gets `503 Retry-After: 1`, and daemon exporters
retain and retry their coalesced batches. Retention purges in bounded chunks so a long purge does
not reject imports for its whole duration.

A push updates findings and per-source observations only. It never clears the poll path's
`unreachable_since_ms`, so a source the Hub cannot reach still reports `unreachable_since` even
while its daemon pushes successfully.

## Read API

- `GET /api/status` reports the Hub service and version, the version of the perf-sentinel binary
  it would run (`engine_version`, null when none is configured), and what a run costs: the worker
  count, the current queue depth, and the trace cap, timeout and report retention it enforces.
- `GET /api/sources` lists every configured source with its kind and its last known collection
  state. Timestamps are null for a source that has never been observed, which a reader must not
  confuse with the epoch, and `producer_version` is null for a trace backend because a backend
  stores traces and detects nothing. `retention_hours` is a declared value, not a measured one,
  and carries the same caveat as the environment: it keeps a stale claim until someone edits it.
- `GET /api/findings` accepts `service`, `finding_type`, `severity`, `limit`, `status`, and the
  daemon-compatible `include_acked` query parameter. `include_acked` defaults to `true`;
  `include_acked=false` hides envelopes carrying a non-null `acknowledged_by`.
- `GET /api/findings/{traceId}` returns findings for a sample trace.
- `GET /api/sources/{sourceId}/daemon` reads one daemon's applied settings and its own account of
  its state, on demand rather than from the poll: settings never change without a restart the Hub
  has no signal for, and the gauges are the point. It answers `404` for an unknown source and
  `400` for a trace backend, which runs no daemon. A daemon that does not answer is reported as
  an observation, `state: "unreachable"` with an error code, and never as a `502`: the Hub is
  relaying a source's health, not failing itself. `config` is the daemon's `[daemon]` section
  relayed verbatim, and is null with `config_unavailable_reason: "api_disabled"` when that daemon
  serves no query API, which is a configuration statement rather than a fault. `detection_config`,
  `scoring_config` and `energy_model` come from the daemon's export instead, where `/api/config`
  does not carry them. `warnings` is the daemon's own tuning advisor verbatim: the Hub relays
  those sentences and writes none of its own. A hint past two thousand characters is cut with a
  visible ellipsis, anything past a hundred hints is counted in `warnings_dropped` rather than
  silently gone, and a failed export read is named in `hints_unavailable_reason` instead of
  reading as a clean bill. An open row re-reads on the interval the reader picks, and
  `?refresh=status` makes that tick a single status read rather than the three the full view
  takes: the export, which is the heavy one, runs at most once a minute, and it is what carries
  the daemon's own hints. A row whose read failed re-reads too, with that same cheap request, and
  the first one that answers is followed straight away by a full read, so a row left open recovers
  by itself instead of waiting to be refolded. The polling this drives cannot starve the daemon:
  the engine's 32-concurrent-request cap is scoped to its OTLP ingest route precisely so `/api`
  and `/health` stay responsive, the status ticks take no Hub read slot at all, and the full reads
  are bounded at two at a time, over pooled connections. The daemon's query surface is HTTP(S) only by design, the gRPC port is
  OTLP ingest, so the Hub speaks no RPC to it. The only thing it derives is `state`, from whether
  a gauge crossed 90 % of its cap, the same line the daemon's own monitor draws. It also carries
  `daemon_defaults`, `detection_defaults` and `defaults_engine_version`, so a reader can mark what
  a daemon actually changed. Those defaults are the ones of the binary this Hub embeds, exactly as
  the engine's own `query monitor` compares against the binary running it, so the version is named
  rather than assumed and a daemon on another minor is flagged instead of judged.

Responses preserve each daemon finding as an opaque, additive JSON document and add durable
`first_seen`, `last_seen`, `max_confidence`, `status`, optional `lineage`, and source freshness
metadata. `first_seen` comes
from the daemon envelope (`first_seen_ms`), clamped to the Hub's observation time and to a
Unix-ms sanity floor, so neither a daemon clock running ahead nor a seconds-unit bug can distort
it, and it falls back to the observation time when a producer omits the field. `last_seen` is
deliberately the Hub's own observation clock: retention, ordering and freshness comparisons rely
on it, so it never comes from a remote clock. IDE clients should ignore unknown fields, as they do
with the daemon API. `/health/live` checks the process; `/health/ready` becomes successful after
SQLite initialization.

`status` is derived at read time, never stored, from data the Hub already keeps: `active` while
the finding was seen within the resolution grace (`Hub:ResolutionGrace`, default 7 days),
`likely_resolved` when the finding went quiet but its endpoint still heartbeats from a reachable
source past the grace, and `not_observed` when nothing proves anything, a silent endpoint or an
unreachable fleet included. It is a presumption, not a verdict: a finding leaving by retention
still leaves silently, but a reader can now tell "the endpoint runs without the finding" apart
from "nobody is looking". `?status=<value>` filters, and the filter applies before the page
limit.

`first_seen` is per signature: a finding whose normalized template changes gets a new signature
and therefore a new `first_seen`. Since schema v2 the Hub links such a mutation to its
predecessor at import time, when exactly one stored finding shares the service, detector and
endpoint with a different template hash, was seen within the last 30 days and strictly before
the incoming batch, and is not itself already superseded. Ambiguity records nothing: naming one
of several candidates would be a guess. A linked finding's envelope carries a `lineage` object
with `original_first_seen` (the earliest birth along the chain) and `predecessors` (the chain
length). Both are denormalized onto the newest link at link time, so a finding's full lineage
survives the retention purge of every earlier hop. The heuristic is conservative and
non-destructive: the two rows stay separate findings, and the predecessor ages out through
normal retention.

## Analysis API

An analysis is a run of the perf-sentinel binary against one configured source,
producing the self-contained HTML dashboard the engine renders.

- `POST /api/analyses` takes `{"source_id": "...", "request": {...}}` and answers `202` with the
  run's id. The request shape follows the source's kind: `{}` for a daemon, which takes no
  parameters at all, `{service, lookback | from_ms + to_ms, max_traces}` for a trace backend, or
  `{trace_id}`. The engine's own exclusions are enforced before anything is queued, so an
  impossible pair is refused rather than discovered as a failed run three minutes later.
- `GET /api/analyses` lists recent runs, newest first, and `GET /api/analyses/{id}` returns one.
- `GET /reports/{id}.html` serves a succeeded run's report, from the same origin as the rest.

A run is two engine invocations, because the query subcommands emit text, JSON or SARIF and only
`report` writes HTML: the source is read into a report JSON, then that JSON is rendered. A daemon
skips the first step, since its own `/api/export/report` already returns one.

Both invocations run from `Hub:Analysis:ReportDirectory`. The engine looks for `.perf-sentinel.toml`
relative to its own working directory, so leaving it unset would let a stray file beside whatever
directory launched the Hub decide detection thresholds for every run.

A request may carry a `detection` object overriding the engine's detection thresholds
(`n_plus_one_min_occurrences`, `window_duration_ms`, `slow_query_threshold_ms`,
`slow_query_min_occurrences`, `max_fanout`, `chatty_service_min_calls`,
`pool_saturation_concurrent_threshold`, `serialized_min_sequential`). Bounds mirror the engine's own
validator, and `GET /api/status` publishes them along with each default under `detection_knobs`. A
value equal to the default is dropped rather than recorded, so a run only carries what actually
departs from the standard configuration. The overrides are written to a per-run TOML handed to both
invocations through `-c`, and deleted when the run ends.

These thresholds decide what counts as a problem, not how the report is written. Raising one does
not make a run lighter, it stops the detector from reporting the smaller cases, which is why counts
from runs with different thresholds are not comparable and the launcher says so. A daemon source
takes none: it detects with its own configuration and the Hub only reads what it already found.

The report's size is not a knob. The sink's 5 MiB target is a private constant with no flag, no
environment variable and no config key, and a report built from a backend query tops out around
4 MB because the share of that budget reserved for embedded span trees is never spent: a backend
query returns findings, not spans. When the sink does drop findings to fit, the run records how
many survived, read back from the rendered file, and the result panel says so above the link.

Every failure is reported as one of eight codes (`source_unreachable`, `source_auth_failed`,
`source_rejected_request`, `timeout`, `output_too_large`, `binary_failed`, `invalid_request`,
`internal`). Raw stderr never leaves the process. It is read to name an owner, since "the backend
refused us" and "the binary broke" have different owners, and that classification is a heuristic
on a bounded set of markers, not a contract.

Reports are deleted `Hub:Analysis:ReportRetention` after they succeed and the run is marked
expired, keeping its parameters. This is not an audit trail, and a link shared yesterday is
already dead. A run still running when the service stops comes back `interrupted` and is never
replayed on its own: a silent retry would fire a second heavy query at a backend nobody asked to
query twice.

## Launcher

The Hub serves a browser interface at `/`, from the same origin as the reports it opens. Plain
HTML, CSS and JavaScript with no framework, no build step and no network fetch: the two typefaces
are base64 in `wwwroot/fonts.css` and every icon is inline SVG.

Four screens: start an analysis, follow one run, list recent runs, and read fleet health. The form
adapts to the selected source's `kind` rather than offering an independent live-or-historical
switch, since a switch would let the operator compose impossible states such as a three-hour window
against a daemon that keeps ten minutes.

A gauge is toned once it is close to a cap it published: red from 90 %, which is the engine's own
advisor line and the same one that turns the row's verdict to near capacity, and amber from 75 %,
which is the Hub's own step ahead of it. Each read also shows what moved since the one
before it, rising off the figure it belongs to and fading out, red for a rise and green for a
fall: every one of
these counts toward a cap, so up is the direction that costs something. Uptime is neither toned nor
tracked, having no cap and only one direction to go in.

Which folds a reader has opened is remembered in that browser's `localStorage`, under one key and
as open folds only: a row, its terminal block, its settings and the groups inside them all come
back the way they were left, and a row left open reads its daemon again on the next visit without
being clicked. Nothing but those names is stored, and a browser that refuses storage simply starts
everything folded.

Neither command carries a placeholder. The endpoint is the source's own configured `BaseUrl`, and
the monitor command carries the re-read interval the reader picked on that row, so a copied line is
runnable as it stands and does not contradict the screen it came from. The one thing an operator
still types is the service name, which is theirs to choose and is shown empty rather than guessed.

Both printed commands say where to get the engine, since neither runs through the Hub: the note
links the release of the exact version this Hub runs, which is the version the flags are spelled
for. Without a probed version the link falls back to the release list rather than inventing a tag.

The launcher also prints the run as an engine command line, so an operator can take it to a
terminal instead. It is built from the very object the form posts, never from the form, so the
printed command and the submitted run cannot drift. It is one command and not the two the Hub
runs: the JSON output and the render step exist so the Hub can build a dashboard, and a terminal
needs neither. Values are quoted for a POSIX shell with single quotes, which is the only form that
holds for a service name carrying `$` or a quote. Detection overrides have no command-line flag,
so a run that changed one carries `-c .perf-sentinel.toml` and the file is printed beside the
command. An authenticated source prints `--auth-header-env` rather than its token, which the Hub
holds and never discloses.

A report rendered from a daemon source goes live when the daemon's `BaseUrl` is a bare origin:
the render passes `--daemon-url`, and the dashboard's own Refresh and acknowledgment controls then
talk to that daemon from the viewer's browser. Two conditions sit outside the Hub: the daemon's
`[daemon.cors] allowed_origins` must carry the origin this Hub serves reports from, and the viewer
must be able to reach the daemon directly. A daemon behind a path-based ingress gets a static
report instead, because the engine's flag takes an origin and nothing else. So does every daemon
source when the configured binary does not take `--daemon-url` at all: the engine declares it
inside its `daemon` feature, so a binary built without that feature refuses the argument rather
than ignoring it, and a run passing it would render nothing. The Hub asks the binary once at
startup, through `report --help`, and renders static when the answer is no or unreadable. Report links share
within network reach of the Hub and die with the retention window.

On the fleet health screen, a daemon's row unfolds into the gauges it reports against their caps
and the hints it writes about its own tuning, neither of which `/metrics` carries. The row re-reads
on an interval the reader picks, the same knob `query monitor --refresh` carries plus an off
position, and a read replaces only the gauges and the hints: settings do not change without a
restart, so rebuilding them would throw away open groups for nothing. Folding the row stops the
reads. The settings themselves are one click further in, grouped and folded, each group showing how
many of its values depart from the engine's own defaults. It ends with the
`perf-sentinel query monitor` command for the same view in a terminal.

The theme is tri-state (system, light, dark). Only the resolved light or dark ever reaches the DOM,
so stylesheets see two values and never three. The position is stored under `perf-sentinel:theme`
in both `localStorage` and `sessionStorage`, the second because the rendered dashboard reads that
exact key from this origin. That handoff is why the launcher and the reports must share an origin.

Nothing from the server is ever written with `innerHTML`. Every displayed string is a text node.

## Freshness and recovery

Push is primary. Each source is also polled independently. A successful poll updates observations but does not delete a
finding merely because a later daemon response omits it: the daemon ring buffer may have evicted
it, so missing does **not** mean resolved. Retention removes findings whose last observation is
older than the configured period.

The perf-sentinel 0.11.x daemon caps `/api/findings` at 1,000 rows. The Hub uses that exact cap and
warns whenever it is reached because the safety-net snapshot may be incomplete; high-volume
coverage therefore requires the bounded push exporter.

Failures keep previously stored findings readable. The affected source is marked unreachable and
retried with bounded exponential backoff; a later success clears that state. Poll bodies are
limited to 16 MiB, requests have a timeout, imports are transactional, and logs identify only the
source ID and a stable error code.

## Backup and restore

`first_seen` history is the one thing the Hub stores that nothing upstream can reconstruct: the
daemon ring buffer forgets, so losing the volume loses the timeline. The binary ships a `backup`
command that snapshots the live database with SQLite `VACUUM INTO`, safe next to the single
writer thanks to WAL. It reads `Hub:DatabasePath` from the same configuration as the server,
refuses to overwrite an existing destination, and removes its partial file when the copy fails.
The snapshot is a full copy written onto the same volume, so keep at least the database's own
size free on the PVC (the chart default is 1Gi total) before starting one.

```bash
kubectl exec deploy/perf-sentinel-hub -- /app/PerfSentinelHub backup /data/hub-backup-20260826.db
```

Date the filename: the overwrite guard makes a fixed path fail on the second run, and the
chiseled runtime image has no shell to delete a leftover with. `kubectl cp` cannot pull the file
from the Hub pod either (no tar inside). Mount the same PVC in a short-lived helper pod, copy
from there, and remove the snapshot from that pod. The volume is `ReadWriteOnce`, so pin the
helper to the node the Hub pod runs on, run it as the Hub's own uid so it can read and delete
the files, and give it the security context a `restricted` namespace demands:

```bash
NODE=$(kubectl get pod -l app.kubernetes.io/name=perf-sentinel-hub -o jsonpath='{.items[0].spec.nodeName}')
kubectl apply -f - <<EOF
apiVersion: v1
kind: Pod
metadata:
  name: hub-backup-fetch
spec:
  nodeName: ${NODE}
  securityContext:
    runAsNonRoot: true
    runAsUser: 1654
    runAsGroup: 1654
    seccompProfile: { type: RuntimeDefault }
  containers:
    - name: fetch
      image: busybox:1.37
      command: ["sleep", "3600"]
      securityContext:
        allowPrivilegeEscalation: false
        capabilities: { drop: ["ALL"] }
      volumeMounts: [{ name: data, mountPath: /data }]
  volumes:
    - name: data
      persistentVolumeClaim: { claimName: perf-sentinel-hub }
EOF
kubectl cp hub-backup-fetch:/data/hub-backup-20260826.db ./hub-backup.db
kubectl exec hub-backup-fetch -- rm /data/hub-backup-20260826.db
kubectl delete pod hub-backup-fetch
```

Locally, `make backup DB=/path/to/hub.db DEST=backup.db` wraps the same command. Restore by
replacing the database file while the Hub is fully stopped: scale the Deployment to zero and
wait for the pod to actually terminate before touching the volume, since graceful shutdown can
take tens of seconds
(`kubectl scale deploy/perf-sentinel-hub --replicas=0` then
`kubectl wait --for=delete pod -l app.kubernetes.io/name=perf-sentinel-hub --timeout=120s`).
Then, from the helper pod, copy the backup over `/data/hub.db`, delete any stale `hub.db-wal`
and `hub.db-shm` next to it, and scale back up.

## Deliberate exclusions

The current codebase has no ingress, user authentication, CI/SARIF import, human interface,
acknowledgment writer, or remote backup (the local `backup` command snapshots
the database, shipping the file off the cluster on a schedule stays the operator's job). Network
exposure and authentication belong in the next independent design; acknowledgments remain in the
repository consumed by perf-sentinel.

## Development

```bash
make verify
make security
make release-check VERSION=0.1.0
```

These are the stable local operator entry points. `make verify` is the local equivalent of the
aggregate build and packaging gate, `make security` runs the configured security checks, and
`make release-check VERSION=0.1.0` validates repository version consistency before signed-tag
creation.
The protected GitHub check is `CI / Gate` from the dedicated PerfSentinel CI Gate App; a same-named
GitHub Actions check does not satisfy that App-backed boundary.

The local gate uses locked packages, tests, a Linux NativeAOT publish, Docker/Trivy, and Helm linting.
The toolchain is pinned to .NET SDK 10.0.400, ASP.NET/SQLite 10.0.11,
SQLitePCLRaw 3.0.5, Helm 4.2.3, and SHA-pinned GitHub Actions: checkout 7.0.1,
setup-dotnet 6.0.0, setup-helm 5.0.1, and Trivy Action 0.36.0. Runtime containers are non-root,
read-only, and based on digest-pinned official NativeAOT/chiseled images.

## Verify a public release in a clean room

After public activation, start from a fresh checkout of the stable tag and download every asset
from its exact GitHub Release URL into a new `release/` directory. No repository secret is needed:

```bash
python3 scripts/verify-release.py public-input \
  https://github.com/robintra/PerfSentinelHub/releases/tag/v0.1.0
python3 scripts/verify-release.py verify-published --root release
```

The first command accepts only a canonical stable-form tag or exact release URL; confirm on that
page that the release is published, not draft or prerelease. The second fails closed unless the
downloaded directory contains exactly the manifest-declared assets and every checksum, subject,
source identity, image digest, chart binding, SBOM, signature bundle, and attestation bundle agrees.
Follow [RELEASING.md](RELEASING.md) for the exact public Cosign and GitHub attestation commands.
Public verification is configured for all four targets, the image by digest, and the chart by
digest; successful observation is deferred to the public rehearsal.

## License

[GNU Affero General Public License v3.0](LICENSE). Applications and IDE plugins communicate with
the Hub over HTTP rather than linking it. If you modify the Hub and offer that modified version
over a network, AGPL section 13 applies. This is a practical summary, not legal advice.
