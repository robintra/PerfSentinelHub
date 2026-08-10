# PerfSentinelHub

PerfSentinelHub gives IDE plugins one durable endpoint for findings collected from one or more
[perf-sentinel 0.11.2](https://github.com/robintra/perf-sentinel/releases/tag/v0.11.2) daemons.
It is a NativeAOT service backed by SQLite: daemon push is the primary path, hourly polling is a
recovery safety net, and the Hub preserves read-compatible finding envelopes for 180 days by default.

## Run locally in five minutes

Requirements: .NET SDK 10.0.302 and a reachable perf-sentinel daemon.

```bash
Hub__DatabasePath=/tmp/perf-sentinel-hub.db \
Hub__Sources__0__Id=local \
Hub__Sources__0__Name='Local daemon' \
Hub__Sources__0__Environment=development \
Hub__Sources__0__BaseUrl=http://localhost:4318 \
Hub__Sources__0__ImportApiKey=0123456789abcdef0123456789abcdef \
ASPNETCORE_URLS=http://localhost:5080 \
dotnet run --project PerfSentinelHub

curl http://localhost:5080/health/ready
curl http://localhost:5080/api/findings
```

The first poll starts immediately. The SQLite file survives restarts at the configured path.

## Install with Helm

The chart always deploys one replica and a persistent volume. Supply at least one source:

```bash
helm upgrade --install perf-sentinel-hub deploy/helm/perf-sentinel-hub \
  --set image.repository=ghcr.io/robintra/perf-sentinel-hub \
  --set image.tag=0.1.0 \
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
| `Hub:DefaultReadLimit` | `1000` | 1–`MaxReadLimit` |
| `Hub:MaxReadLimit` | `10000` | 1–10000 |
| `Hub:Sources` | none | At least one source |
| `Sources[].Id` | none | Non-empty and unique |
| `Sources[].Name` | none | Non-empty |
| `Sources[].Environment` | none | Non-empty |
| `Sources[].BaseUrl` | none | Required; absolute HTTP(S) URL without credentials, query, or fragment. A path prefix is kept, so `https://gw/perf-sentinel/` polls `https://gw/perf-sentinel/api/status` |
| `Sources[].AuthHeaderName/Value` | none | Both absent or both present; no newlines |
| `Sources[].ImportApiKey` | none | Optional push credential; at least 32 characters, supplied through a Secret |

## Import API

`POST /api/import/findings?source_id=<id>` accepts the daemon's JSON envelope
`{"producer_version":"…","findings":[…]}` with `X-API-Key`. A request contains 1–100 findings
and at most 2 MiB. The response is sent only after the idempotent signature upsert commits.

The Hub admits one import at a time, matching SQLite's single-writer model. Concurrent imports get
`503 Retry-After: 1`; daemon exporters retain and retry their coalesced batches. This bounds request
memory and garbage-collector pressure independently of the number of daemons.

## Read API

- `GET /api/status` reports the Hub service and version.
- `GET /api/findings` accepts `service`, `finding_type`, `severity`, `limit`, and the
  daemon-compatible `include_acked` query parameter. `include_acked` defaults to `true`;
  `include_acked=false` hides envelopes carrying a non-null `acknowledged_by`.
- `GET /api/findings/{traceId}` returns findings for a sample trace.

Responses preserve each daemon finding as an opaque, additive JSON document and add durable
`first_seen`, `last_seen`, `max_confidence`, and source freshness metadata. IDE clients should
ignore unknown fields, as they do with the daemon API. `/health/live` checks the process;
`/health/ready` becomes successful after SQLite initialization.

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

## Deliberate exclusions

This release has no ingress, user authentication, CI/SARIF import, worker execution, trace
backend, dashboard, acknowledgment writer, or remote backup. Network exposure and authentication
belong in the next independent design; acknowledgments remain in the repository consumed by
perf-sentinel.

## Development

```bash
make verify
```

The gate uses locked packages, tests, a linux NativeAOT publish, Docker/Trivy, and Helm linting.
The toolchain is pinned to .NET SDK 10.0.302, ASP.NET/SQLite 10.0.10,
SQLitePCLRaw 3.0.5, Helm 4.2.3, and SHA-pinned GitHub Actions: checkout 7.0.1,
setup-dotnet 6.0.0, setup-helm 5.0.1, and Trivy Action 0.36.0. Runtime containers are non-root,
read-only, and based on digest-pinned official NativeAOT/chiseled images.

## License

[GNU Affero General Public License v3.0](LICENSE). Applications and IDE plugins communicate with
the Hub over HTTP rather than linking it. If you modify the Hub and offer that modified version
over a network, AGPL section 13 applies. This is a practical summary, not legal advice.
