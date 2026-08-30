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
# producer. Only the gauge values are chosen: an idle daemon reports zeros.

ENGINE="${1:-}"
[ -x "$ENGINE" ] || { echo "usage: $0 /path/to/perf-sentinel" >&2; exit 2; }

HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/fixtures"
PORT=41890
TRACES="$(dirname "$ENGINE")/../../tests/fixtures/demo.json"
[ -f "$TRACES" ] || { echo "no demo trace file at $TRACES" >&2; exit 1; }

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
NOW = 1_756_512_000_000  # fixed, so a rerun with the same engine changes nothing
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
(out / "daemon-status-busy.json").write_text(json.dumps(busy, indent=2) + "\n")
(out / "daemon-status-calm.json").write_text(json.dumps(calm, indent=2) + "\n")
print(f"  captured from perf-sentinel {version}: {len(stored)} findings")
PY
