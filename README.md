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
| `Hub:Analysis:Workers` | `2` | 1–16 |
| `Hub:Analysis:MaxTracesCap` | `2000` | 1–100000 |
| `Hub:Analysis:Timeout` | `00:05:00` | Positive, at most one hour |
| `Hub:Analysis:ReportRetention` | `1.00:00:00` (24 hours) | Positive duration |
| `Hub:Sources` | none | At least one source |
| `Sources[].Id` | none | Non-empty and unique |
| `Sources[].Name` | none | Non-empty |
| `Sources[].Environment` | none | Non-empty |
| `Sources[].Kind` | `daemon` | One of `daemon`, `tempo`, `jaeger_query`. Only a daemon is polled and only a daemon may carry an import key |
| `Sources[].BaseUrl` | none | Required; absolute HTTP(S) URL without credentials, query, or fragment. A path prefix is kept, so `https://gw/perf-sentinel/` polls `https://gw/perf-sentinel/api/status` |
| `Sources[].AuthHeaderName/Value` | none | Both absent or both present; no newlines |
| `Sources[].ImportApiKey` | none | Optional push credential; at least 32 characters, supplied through a Secret |

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
  stores traces and detects nothing.
- `GET /api/findings` accepts `service`, `finding_type`, `severity`, `limit`, `status`, and the
  daemon-compatible `include_acked` query parameter. `include_acked` defaults to `true`;
  `include_acked=false` hides envelopes carrying a non-null `acknowledged_by`.
- `GET /api/findings/{traceId}` returns findings for a sample trace.

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

The current codebase has no ingress, user authentication, CI/SARIF import, worker execution, trace
backend, dashboard, acknowledgment writer, or remote backup (the local `backup` command snapshots
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
