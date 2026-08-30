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
than through a library: six metric families over data the Hub already holds do
not justify a dependency in a service whose only two packages are SQLite.

| Metric                                          | Type  | What it answers                                     |
|-------------------------------------------------|-------|-----------------------------------------------------|
| `perf_sentinel_hub_build_info{version}`         | gauge | Which version is running                            |
| `perf_sentinel_hub_source_reachable{source}`    | gauge | Whether the last poll of a daemon succeeded         |
| `perf_sentinel_hub_source_unreachable_seconds`  | gauge | How long it has been unreachable, 0 when it answers |
| `perf_sentinel_hub_source_last_success_seconds` | gauge | Age of the last successful poll                     |
| `perf_sentinel_hub_analysis_queue_depth`        | gauge | Runs accepted and not yet claimed by a worker       |
| `perf_sentinel_hub_analysis_runs{status}`       | gauge | Runs currently stored, per status                   |

Three things the shape is deliberate about.

Only a daemon gets a source series. A trace backend is never polled, so calling
it reachable would assert something the Hub has not observed.

A daemon never polled successfully gets no `last_success` series at all. Zero
would read as "succeeded just now", which is the opposite of never.

`analysis_runs` is a gauge, not a `_total` counter. Retention deletes rows, so
the series falls as well as rises, and every status is emitted even at zero
because a gauge that vanishes reads as a scrape failure rather than as "nothing
is in that state".

Cardinality is bounded by configuration. `source` takes the ids in
`Hub:Sources`, fixed at startup and restricted to 1 to 64 ASCII letters, digits,
`.`, `_` or `-`. `status` takes six constants. Nothing a caller sends reaches a
label.

The endpoint carries no authentication, exactly like `/api/status`. Keep it
behind whatever fronts the rest of the Hub. The chart leaves the scrape opted
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
| [`grafana-dashboard.json`](../examples/grafana-dashboard.json) | Eight panels over the six families, importable as it stands |
| [`prometheus-alerts.yml`](../examples/prometheus-alerts.yml)   | Five rules, checked with `promtool check rules`             |
| [`prometheus-scrape.yml`](../examples/prometheus-scrape.yml)   | A scrape job for a deployment that names its targets        |

The engine ships its own dashboard for its own metrics, and the two do not
overlap: no panel here reads a daemon series, and no panel there reads a Hub
one. Import both to watch a fleet and the Hub collecting it.

One rule cannot be written. A daemon never polled successfully publishes no
`last_success` series at all, and Prometheus holds no list of the sources that
ought to exist to compare against, so nothing alerts on a source that has never
answered. The fleet screen is where that shows.

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
