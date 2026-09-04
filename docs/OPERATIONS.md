# Operations

## Freshness and recovery

Push is primary. Each source is also polled independently, as a safety net.

A successful poll updates observations. It does not delete a finding merely because a
later daemon response omits it: the daemon's ring buffer may have evicted it, so missing
does **not** mean resolved. Only retention removes findings, when their last observation
is older than the configured period.

The perf-sentinel 0.11.x daemon caps `/api/findings` at 1,000 rows. The Hub uses that
exact cap and warns whenever it is reached, because the safety-net snapshot may be
incomplete. High-volume coverage therefore requires the bounded push exporter.

Failures keep previously stored findings readable. The affected source is marked
unreachable and retried with bounded exponential backoff, and a later success clears that
state. Poll bodies are limited to 16 MiB, requests have a timeout, imports are
transactional, and logs identify only the source ID and a stable error code.

## Metrics

`GET /metrics` serves the Prometheus text format. It is written by hand rather
than through a library: eight metric families over data the Hub already holds do
not justify a dependency in a service whose only two packages are SQLite.

| Metric                                          | Type  | What it answers                                     |
|-------------------------------------------------|-------|-----------------------------------------------------|
| `perf_sentinel_hub_build_info{version}`         | gauge | Which version is running                            |
| `perf_sentinel_hub_source_reachable{source}`    | gauge | Whether the last poll of a daemon succeeded         |
| `perf_sentinel_hub_source_unreachable_seconds`  | gauge | How long it has been unreachable, 0 when it answers |
| `perf_sentinel_hub_source_last_success_seconds` | gauge | Age of the last successful poll                     |
| `perf_sentinel_hub_source_last_import_seconds`  | gauge | When a daemon last pushed. Not a heartbeat, see below |
| `perf_sentinel_hub_import_rejected_total{reason}` | counter | Imports refused, by reason                       |
| `perf_sentinel_hub_analysis_queue_depth`        | gauge | Runs accepted and not yet claimed by a worker       |
| `perf_sentinel_hub_analysis_runs{status}`       | gauge | Runs currently stored, per status                   |

Three things the shape is deliberate about.

Only a daemon gets a source series, and only one the Hub has actually observed.
A trace backend is never polled, and a daemon with no `source_state` row has
never been reached at all, so publishing a value for either would assert
something the Hub has not seen. That row is also what retention drops for a
source it stopped attempting, so a long forgotten source goes silent rather
than turning green.

A daemon never polled successfully gets no `last_success` series at all. Zero
would read as "succeeded just now", which is the opposite of never.

`analysis_runs` is a gauge, not a `_total` counter. A run moves between statuses,
and a finished one ages out on `Hub:Analysis:RunRetention`, so every series falls
as well as rises. A run still `pending` or `running` is never purged however old
its row looks, since a worker is about to write to it or already is. Every status
is emitted even at zero, a gauge that vanishes reading as a scrape failure rather
than as "nothing is in that state".

Cardinality is bounded by configuration. `source` takes the ids in
`Hub:Sources`, fixed at startup and restricted to 1 to 64 ASCII letters, digits,
`.`, `_` or `-`. `status` takes six constants. Nothing a caller sends reaches a
label.

The endpoint carries no authentication, exactly like `/api/status`. Keep it
behind whatever fronts the rest of the Hub. It shares that origin with the
launcher, so a proxy opened for the browser serves `/metrics` as well unless
the path is excluded. The chart leaves the scrape opted
into rather than assumed:

```yaml
service:
  annotations:
    prometheus.io/scrape: "true"
    prometheus.io/port: "8080"
    prometheus.io/path: /metrics
```

### Consuming it

Three files under [`examples/`](../examples), each validated rather than sketched.

| File                                                           | Is                                                          |
|----------------------------------------------------------------|-------------------------------------------------------------|
| [`grafana-dashboard.json`](../examples/grafana-dashboard.json) | Nine panels over the eight families, importable as it stands |
| [`prometheus-alerts.yml`](../examples/prometheus-alerts.yml)   | One rule, checked with `promtool check rules`               |
| [`prometheus-scrape.yml`](../examples/prometheus-scrape.yml)   | A scrape job for a deployment that names its targets        |

The engine ships its own dashboard for its own metrics, and the two do not
overlap: no panel here reads a daemon series, and no panel there reads a Hub
one. Import both to watch a fleet and the Hub collecting it.

### Why there is only one alert

The Hub sits in no production request path, and push is the primary path: a
daemon POSTs its findings and retains and retries coalesced batches. So almost
nothing the Hub can report is worth waking anyone, and an alert that fires on a
condition a reader could have seen on a panel is noise. Four rules were written
and cut:

| Cut                       | Why, and where the condition shows instead                                                                                                                                     |
|---------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| A source is unreachable   | It watches the poll safety net, not the push path, so it goes red on a fleet whose daemons are all delivering. The fleet screen and two dashboard panels already draw it        |
| A source has gone stale   | Same blindness, and by the time a day has passed the last-success panel has been drawing the climb for a day                                                                    |
| The analysis queue backs up | Human-submitted work: a depth of 20 is 20 people clicking run. `GET /api/status` shows it to the very person who queued them, and nothing is lost while they wait               |
| Runs were interrupted     | Fires on the most routine event there is, a restart that caught a queued run, and never clears since nothing deletes those rows                                                 |

What survives is the one condition no dashboard can show, because a dead Hub
publishes no series and every panel goes blank exactly like a broken scrape
config.

The import counter is new and still does not earn a rule, which is worth stating
because it looks like it should. `unauthorized` rises when a key expires and
also when anyone at all posts an unknown `source_id`, and the Hub cannot tell
those apart: the only label that would separate them is the caller's own
`source_id`, which is exactly the unbounded value that must never reach a label.
Alerting on it would hand a stranger the ability to page you, and so would
`bad_request`, whose query-string half is checked before the key is.
`gate_full` and `write_timeout` are backpressure a daemon retries through, kept
apart because they name different knobs: too many uploaders at once against a
writer held by the poll or by retention. The reachable-only-with-a-key half of
`bad_request`, like `too_large`, is fixed in the exporter's repo rather than
here. All four belong on the panel, where a
human reads them with the context that tells them apart.

Two gaps remain, worth naming rather than papering over. A push blocked before
it arrives, by a network policy for instance, produces no request and therefore
no rejection, so the counter cannot see the most common breakage, and
`source_last_import_seconds` cannot either, since a daemon with no new findings
pushes nothing and looks identical. Telling those apart needs per-finding
provenance the Hub does not store. And Prometheus holds no list of the sources
that ought to exist, so a daemon the Hub has never reached is silent rather than
alarming, which is a case for the fleet screen.

## Backup

`first_seen` history is the one thing the Hub stores that nothing upstream can
reconstruct. The daemon's ring buffer forgets, so losing the volume loses the timeline.

The binary ships a `backup` command that snapshots the live database with SQLite
`VACUUM INTO`, safe next to the single writer thanks to WAL. It reads `Hub:DatabasePath`
from the same configuration as the server, refuses to overwrite an existing destination,
and removes its partial file when the copy fails.

The snapshot is a full copy written onto the same volume, so keep at least the database's
own size free on the PVC before starting one. The chart default is 1Gi total.

```bash
kubectl exec deploy/perf-sentinel-hub -- /app/PerfSentinelHub backup /data/hub-backup-20260826.db
```

Date the filename. The overwrite guard makes a fixed path fail on the second run, and the
chiseled runtime image has no shell to delete a leftover with.

### Getting the file off the cluster

`kubectl cp` cannot pull from the Hub pod, which has no tar inside. Mount the same PVC in
a short-lived helper pod, copy from there, and remove the snapshot from that pod.

The volume is `ReadWriteOnce`, so pin the helper to the node the Hub pod runs on. Run it as
the Hub's own uid so it can read and delete the files, and give it the security context a
`restricted` namespace demands:

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

Locally, `make backup DB=/path/to/hub.db DEST=backup.db` wraps the same command.

## Restore

Replace the database file while the Hub is fully stopped. Scale the Deployment to zero and
wait for the pod to actually terminate before touching the volume, since graceful shutdown
can take tens of seconds:

```bash
kubectl scale deploy/perf-sentinel-hub --replicas=0
kubectl wait --for=delete pod -l app.kubernetes.io/name=perf-sentinel-hub --timeout=120s
```

Then, from the helper pod, copy the backup over `/data/hub.db`, delete any stale
`hub.db-wal` and `hub.db-shm` next to it, and scale back up.
