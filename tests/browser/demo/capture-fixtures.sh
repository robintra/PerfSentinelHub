#!/usr/bin/env bash
set -euo pipefail

# Refreshes demo/fixtures/ from a real perf-sentinel daemon, so the screenshots
# keep showing what the engine actually answers rather than what someone once
# thought it answered. Run it when the engine version moves.
#
#   bash demo/capture-fixtures.sh /path/to/perf-sentinel
#
# api/config comes from a live daemon verbatim. The findings come from
# `analyze --format json` on the engine's own demo trace file, because the OTLP
# listener takes protobuf only and feeding a daemon would mean standing up a
# producer. The incidents and the acks come from daemons this script feeds and
# then reads. Only the gauge values are chosen: an idle daemon reports zeros.
#
# One line does not hold still between captures. The chatty finding's suggestion
# names two of the sixteen calls it counted, and the engine picks them from an
# unordered collection where all sixteen are tied, so a rerun against the very
# same binary names two other services. Both readings are real engine output, so
# a diff confined to that sentence is noise rather than a change to chase.

ENGINE="${1:-}"
[[ -x "$ENGINE" ]] || { echo "usage: $0 /path/to/perf-sentinel" >&2; exit 2; }

HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/fixtures"
PORT=41890
TRACES="$(dirname "$ENGINE")/../../tests/fixtures/demo.json"
[[ -f "$TRACES" ]] || { echo "no demo trace file at $TRACES" >&2; exit 1; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

"$ENGINE" watch --listen-port-http "$PORT" --listen-port-grpc $((PORT + 1)) > "$work/daemon.log" 2>&1 &
daemon=$!
trap 'kill $daemon 2>/dev/null || true; rm -rf "$work"' EXIT

# Read over the loopback from Python rather than curl. check-supply-chain.py
# treats any curl or wget in a .sh as a supply-chain download and holds it to the
# canonical /bin/dash download-script shape, which this is not: nothing here
# crosses the network, it polls a daemon this script just started.
python3 - "$PORT" "$work" <<'WAIT'
import json, pathlib, sys, time, urllib.error, urllib.request

port, work = sys.argv[1], pathlib.Path(sys.argv[2])
base = f"http://127.0.0.1:{port}"


def fetch(path, deadline=0.0):
    while True:
        try:
            with urllib.request.urlopen(f"{base}/{path}", timeout=2) as answer:
                return answer.read()
        except (urllib.error.URLError, OSError):
            if time.monotonic() >= deadline:
                raise
            time.sleep(0.25)


try:
    status = fetch("api/status", deadline=time.monotonic() + 15)
except Exception:
    sys.exit("the daemon never answered")
(work / "status.json").write_bytes(status)
(work / "config.json").write_bytes(fetch("api/config"))
WAIT
"$ENGINE" analyze --input "$TRACES" --format json > "$work/report.json"
kill $daemon 2>/dev/null || true

VERSION="$("$ENGINE" --version | awk '{print $2}')"
python3 - "$work" "$OUT" "$VERSION" <<'PY'
import json, sys, pathlib
work, out, version = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2]), sys.argv[3]

cfg = json.loads((work / "config.json").read_text())
cfg["environment"] = "production"
cfg["max_active_traces"] = 1000
cfg["cors_allowed_origins"] = ["http://127.0.0.1:8080"]
(out / "daemon-config.json").write_text(json.dumps(cfg, indent=2) + "\n")

rep = json.loads((work / "report.json").read_text())
rep["warnings"] = ["ingest queue is undersized for the observed span rate"]
rep["warning_details"] = [{"kind": "tuning",
                           "message": "ingest queue is undersized for the observed span rate"}]
(out / "daemon-report.json").write_text(json.dumps(rep, indent=2) + "\n")

# StoredFinding, from crates/sentinel-core/src/daemon/findings_store.rs.
NOW = 1_756_512_000_000  # fixed, so the stamps derived below do not move
stored = [{"finding": f,
           "stored_at_ms": NOW - i * 37_000,
           "first_seen_ms": NOW - 86_400_000 * (i % 5 + 1),
           "seen_count": (i * 7) % 23 + 1}
          for i, f in enumerate(rep["findings"])]
(out / "daemon-findings.json").write_text(json.dumps(stored, indent=2) + "\n")

base = json.loads((work / "status.json").read_text())
# 888_120 s is 10 d 6 h 42 m: a round uptime would hide the units the gauge shows.
busy = base | {"uptime_seconds": 888_120, "active_traces": 912, "max_active_traces": 1000,
               "analysis_queue_depth": 41, "analysis_queue_capacity": 1024,
               "stored_findings": len(stored), "max_retained_findings": 10_000}
# One minor behind the Hub's own engine, on purpose: a fleet where every daemon
# matches never shows the skew badge, and that badge is the reason the launcher
# prints a producer version at all. Derived rather than written, so it stays one
# minor behind whatever the next capture reports.
major, minor, *_ = (base["version"].split(".") + ["0", "0"])[:3]
behind = f"{major}.{max(int(minor) - 1, 0)}.0"
calm = base | {"version": behind,
               # 263_220 s is 3 d 1 h 7 m.
               "uptime_seconds": 263_220, "active_traces": 128, "max_active_traces": 1000,
               "analysis_queue_depth": 2, "analysis_queue_capacity": 1024,
               "stored_findings": 4, "max_retained_findings": 10_000}
# The two daemons the ack page needs beside those, on the captured version
# rather than behind it: the Hub never asks a daemon below 0.24.0 for its ack
# listing, and a daemon it never asked has no ack state to show. Their own
# gauges, because the fleet screen reads every daemon and two identical panels
# read as one panel drawn twice.
# 511_500 s is 5 d 22 h 5 m, 97_380 s is 1 d 3 h 3 m.
orders = base | {"uptime_seconds": 511_500, "active_traces": 344, "max_active_traces": 1000,
                 "analysis_queue_depth": 7, "analysis_queue_capacity": 1024,
                 "stored_findings": len(stored), "max_retained_findings": 10_000}
ci = base | {"uptime_seconds": 97_380, "active_traces": 61, "max_active_traces": 1000,
             "analysis_queue_depth": 0, "analysis_queue_capacity": 1024,
             "stored_findings": 6, "max_retained_findings": 10_000}
(out / "daemon-status-busy.json").write_text(json.dumps(busy, indent=2) + "\n")
(out / "daemon-status-calm.json").write_text(json.dumps(calm, indent=2) + "\n")
(out / "daemon-status-acked.json").write_text(json.dumps(orders, indent=2) + "\n")
(out / "daemon-status-baseline.json").write_text(json.dumps(ci, indent=2) + "\n")
print(f"  captured from perf-sentinel {version}: {len(stored)} findings")
PY

# The incidents come from a second daemon run, with [daemon.incidents] on and a
# short lookback. The first run stays bare so api/config keeps reporting the
# defaults the Sources screen contrasts a real deployment against.
#
# The sequence is what produces the states the screen has to be able to show,
# and none of them can be written by hand: the daemon decides the window, the
# frozen findings and the capture verdict. One incident is posted before any
# finding exists (empty ring), two are posted with a startsAt of now (the ring
# reaches below the window, complete), two with an older startsAt (the window
# opens before the ring does, partial), and one of those is then resolved.
INC_PORT=$((PORT + 4))
INC_KEY="capture-incident-key"
cat > "$work/incidents.toml" <<EOF
[daemon]
listen_address = "127.0.0.1"
listen_port_http = ${INC_PORT}
listen_port_grpc = $((INC_PORT + 1))
json_socket = "$work/s"
trace_ttl_ms = 1000
api_enabled = true

[daemon.incidents]
enabled = true
api_key = "${INC_KEY}"
lookback_ms = 3000

[detection]
n_plus_one_min_occurrences = 5
EOF
"$ENGINE" watch --config "$work/incidents.toml" > "$work/incidents-daemon.log" 2>&1 &
daemon=$!
trap 'kill $daemon 2>/dev/null || true; rm -rf "$work"' EXIT

python3 - "$INC_PORT" "$INC_KEY" "$work" <<'CAPTURE'
import json, socket, sys, time, urllib.error, urllib.request
from datetime import datetime, timezone

port, key, work = sys.argv[1], sys.argv[2], sys.argv[3]
base, sock_path = f"http://127.0.0.1:{port}", f"{work}/s"


def rfc3339(ms):
    stamp = datetime.fromtimestamp(ms / 1000, timezone.utc)
    return stamp.strftime("%Y-%m-%dT%H:%M:%S.") + f"{ms % 1000:03d}Z"


def post(alerts):
    body = json.dumps({"version": "4", "alerts": alerts}).encode()
    request = urllib.request.Request(base + "/api/incidents", data=body, method="POST")
    request.add_header("content-type", "application/json")
    request.add_header("X-API-Key", key)
    with urllib.request.urlopen(request, timeout=5) as answer:
        return json.loads(answer.read())


def alert(service, kind, at_ms, summary, ended_ms=None, namespace=None):
    # The namespace label kube-prometheus attaches, left off one alert so the
    # screen also shows the empty cell. The daemon hashes it into the id, so a
    # resolve must carry the same one as the firing it closes.
    labels = {"service": service, "perf_sentinel_kind": kind}
    if namespace:
        labels["namespace"] = namespace
    return {"status": "resolved" if ended_ms else "firing",
            "labels": labels,
            "annotations": {"summary": summary},
            "startsAt": rfc3339(at_ms),
            "endsAt": rfc3339(ended_ms) if ended_ms else "0001-01-01T00:00:00Z"}


def seed(service, table, endpoint, suffix):
    """One trace of eight sibling SELECTs, an n+1 over the configured floor."""
    now = datetime.now(timezone.utc)
    stamp = now.strftime("%Y-%m-%dT%H:%M:%S.") + f"{now.microsecond // 1000:03d}Z"
    trace = (f"hub{suffix}" + "0" * 29)[:32]
    events = [{"timestamp": stamp, "trace_id": trace, "span_id": f"s{i:015d}",
               "service": service, "cloud_region": "eu-west-3",
               "type": "sql", "operation": "SELECT",
               "target": f"SELECT * FROM {table} WHERE owner_id = {i}",
               "duration_us": 1500,
               "source": {"endpoint": endpoint, "method": f"{service}::list"}}
              for i in range(1, 9)]
    connection = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    connection.connect(sock_path)
    connection.sendall((json.dumps(events) + "\n").encode())
    connection.close()


deadline = time.monotonic() + 20
while True:
    try:
        urllib.request.urlopen(base + "/api/status", timeout=2).read()
        break
    except (urllib.error.URLError, OSError):
        if time.monotonic() >= deadline:
            sys.exit("the incidents daemon never answered")
        time.sleep(0.25)

now_ms = lambda: int(time.time() * 1000)
# Nothing has been analysed yet, so this one freezes an empty ring. Its settle
# pass re-resolves the window one TTL after it closes and would find the seeded
# findings, so the seeding waits for that pass to have run.
post([alert("gateway-svc", "deploy", now_ms(), "rollout 4f21c8 reached every replica", namespace="edge")])
time.sleep(4.5)
seed("order-svc", "orders", "GET /orders", "a")
seed("cache-svc", "sessions", "GET /sessions", "b")
time.sleep(2.5)
seed("checkout-svc", "invoices", "POST /checkout", "c")
seed("reports-svc", "exports", "GET /reports", "d")
time.sleep(2.5)

at = now_ms()
# Two windows that open after the ring did, so their capture is complete, and
# two that open before it, so the daemon reports a partial one.
post([alert("checkout-svc", "oom_kill", at, "container exceeded its memory limit", namespace="shop"),
      alert("reports-svc", "memory_saturation", at, "working set at 94 percent of the limit",
            namespace="reporting"),
      alert("order-svc", "restart", at - 4000, "pod restarted by the kubelet", namespace="shop"),
      # An unrecognised kind folds to other rather than minting a label.
      alert("cache-svc", "node_pressure", at - 4000, "node under memory pressure, pod evicted")])
post([alert("order-svc", "restart", at - 4000, "pod restarted by the kubelet", ended_ms=at + 41_000,
            namespace="shop")])

request = urllib.request.Request(base + "/api/incidents?limit=100")
request.add_header("X-API-Key", key)
with urllib.request.urlopen(request, timeout=5) as answer:
    incidents = json.loads(answer.read())
open(f"{work}/incidents.json", "w").write(json.dumps(incidents))
CAPTURE
kill $daemon 2>/dev/null || true

python3 - "$work" "$OUT" <<'PY'
import json, pathlib, sys
work, out = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
incidents = json.loads((work / "incidents.json").read_text())


def verdict(incident):
    oldest = incident.get("oldest_finding_ms")
    if oldest is None:
        return "empty ring"
    return "complete" if oldest <= incident["window_from_ms"] else "partial"


kinds = {i["kind"] for i in incidents}
verdicts = {verdict(i) for i in incidents}
# The screen is only worth a screenshot when it shows the states apart. A
# capture that lost one of them is a capture to run again, not one to ship.
if len(kinds) < 5 or len(verdicts) < 3:
    sys.exit(f"captured {sorted(kinds)} and {sorted(verdicts)}, "
             "expected five kinds and three capture verdicts")
namespaces = {i.get("namespace") for i in incidents}
if None not in namespaces or len(namespaces) < 3:
    sys.exit(f"captured namespaces {sorted(n or '' for n in namespaces)}, "
             "expected the three labels and one alert without")

# One file per fake daemon, the way the status fixture already splits, so the
# fleet reads as two daemons with their own incidents rather than one record
# duplicated. The busy one keeps the memory events, the calm one the rest.
memory = {"oom_kill", "memory_saturation"}
busy = [i for i in incidents if i["kind"] in memory]
calm = [i for i in incidents if i["kind"] not in memory]
(out / "daemon-incidents-busy.json").write_text(json.dumps(busy, indent=2) + "\n")
(out / "daemon-incidents-calm.json").write_text(json.dumps(calm, indent=2) + "\n")
print(f"  captured {len(incidents)} incidents: {', '.join(sorted(kinds))}")
print(f"  capture verdicts: {', '.join(sorted(verdicts))}")
PY

# The acks come from two more daemon runs, and two is the minimum: one daemon
# cannot hold both kinds of ack on one signature. It answers 409 to an ack of
# its own on a signature its CI baseline already carries, and says to edit the
# file instead. So one run takes the ack at runtime, the other loads a baseline
# that holds it, and the ack page shows both rows on the same finding.
#
# The ack on the signature the page is captured on never expires. The Hub serves
# a mirrored ack only while its expiry is ahead, so a dated one would empty that
# row a few months after the capture and the screen would read as if nobody had
# ever acknowledged anything. The second ack, on a finding the page does not
# show, carries an expiry, so that field is captured from a daemon that accepted
# it rather than written by hand.
ACK_PORT=$((PORT + 8))
ACK_KEY="capture-ack-key"

python3 - "$work" <<'PY'
import json, pathlib, sys
from datetime import datetime, timezone

work = pathlib.Path(sys.argv[1])
report = json.loads((work / "report.json").read_text())
# The first finding is the one the page is captured on, the critical n+1 every
# daemon of the fleet carries. The second is any other, so a listing holds more
# than the single row the page reads.
page = report["findings"][0]["signature"]
other = next(f["signature"] for f in report["findings"] if f["signature"] != page)
(work / "signatures.json").write_text(json.dumps({"page": page, "other": other}))
(work / "acks.toml").write_text(
    "[[acknowledged]]\n"
    f'signature = "{page}"\n'
    'acknowledged_by = "platform-guild@example.com"\n'
    f'acknowledged_at = "{datetime.now(timezone.utc).date().isoformat()}"\n'
    'reason = "Carried in the baseline until the batched query ships."\n')
PY

for round in runtime baseline; do
  ack_port=$ACK_PORT
  ack_toml=""
  if [[ "$round" == "baseline" ]]; then
    ack_port=$((ACK_PORT + 2))
    ack_toml="toml_path = \"$work/acks.toml\""
  fi
  cat > "$work/ack-$round.toml" <<EOF
[daemon]
listen_address = "127.0.0.1"
listen_port_http = ${ack_port}
listen_port_grpc = $((ack_port + 1))
api_enabled = true

[daemon.ack]
enabled = true
api_key = "${ACK_KEY}"
storage_path = "$work/acks-$round.jsonl"
${ack_toml}
EOF
  "$ENGINE" watch --config "$work/ack-$round.toml" > "$work/ack-$round.log" 2>&1 &
  daemon=$!
  trap 'kill $daemon 2>/dev/null || true; rm -rf "$work"' EXIT

  python3 - "$ack_port" "$ACK_KEY" "$round" "$work" <<'ACKS'
import json, pathlib, sys, time, urllib.error, urllib.parse, urllib.request
from datetime import datetime, timedelta, timezone

port, key, which, work = sys.argv[1], sys.argv[2], sys.argv[3], pathlib.Path(sys.argv[4])
base = f"http://127.0.0.1:{port}"
signatures = json.loads((work / "signatures.json").read_text())


def call(path, method, body=None):
    request = urllib.request.Request(base + path, data=body, method=method)
    if body is not None:
        request.add_header("content-type", "application/json")
    request.add_header("X-API-Key", key)
    with urllib.request.urlopen(request, timeout=5) as answer:
        return answer.read()


def ack(signature, by, reason, expires_at=None):
    body = {"by": by, "reason": reason}
    if expires_at:
        body["expires_at"] = expires_at
    call(f"/api/findings/{urllib.parse.quote(signature, safe='')}/ack", "POST",
         json.dumps(body).encode())


deadline = time.monotonic() + 20
while True:
    try:
        urllib.request.urlopen(base + "/api/status", timeout=2).read()
        break
    except (urllib.error.URLError, OSError):
        if time.monotonic() >= deadline:
            sys.exit("the acks daemon never answered")
        time.sleep(0.25)

ack(signatures["other"], "cache-guild@example.com",
    "Known, the read-through cache lands in front of it.",
    (datetime.now(timezone.utc) + timedelta(days=365)).strftime("%Y-%m-%dT%H:%M:%SZ"))
if which == "runtime":
    ack(signatures["page"], "order-oncall@example.com",
        "Known since the incident, the batched query ships with the order service.")
(work / f"acks-{which}.json").write_bytes(call("/api/acks?include_toml=true", "GET"))
ACKS
  kill $daemon 2>/dev/null || true
done

python3 - "$work" "$OUT" <<'PY'
import json, pathlib, sys
work, out = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
page = json.loads((work / "signatures.json").read_text())["page"]
runtime = json.loads((work / "acks-runtime.json").read_text())
baseline = json.loads((work / "acks-baseline.json").read_text())


def held_by(rows, source):
    return [row for row in rows if row["signature"] == page and row["source"] == source]


# Sorted, so a rerun against the same engine changes nothing: the daemon lists
# in the order it wrote, which is the order the requests happened to land in.
def ordered(rows):
    return sorted(rows, key=lambda row: (row["signature"], row["source"]))


# Three listings, one per row the ack page has to be able to draw on the same
# finding. A capture that lost one of them is a capture to run again.
unacked = [row for row in runtime if row["signature"] != page]
if len(held_by(runtime, "daemon")) != 1 or len(held_by(baseline, "toml")) != 1 or not unacked:
    sys.exit(f"captured {len(runtime)} runtime and {len(baseline)} baseline acks, "
             "expected a daemon ack and a baseline ack on the page's signature "
             "and one ack on another finding")

(out / "daemon-acks-busy.json").write_text(json.dumps(ordered(unacked), indent=2) + "\n")
(out / "daemon-acks-acked.json").write_text(json.dumps(ordered(runtime), indent=2) + "\n")
(out / "daemon-acks-baseline.json").write_text(json.dumps(ordered(baseline), indent=2) + "\n")
print(f"  captured acks on {page}")
PY
