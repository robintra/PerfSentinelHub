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
