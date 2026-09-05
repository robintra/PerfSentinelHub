// Replays a real daemon's five responses so the launcher has something to draw.
//
// The bodies in fixtures/ are captures from a real 0.16.0 daemon, not
// hand-written shapes: the findings come from `analyze --format json` on the
// engine's own demo fixture, and the config is that daemon's `api/config`
// verbatim. Only the gauge values in the status files are chosen, because an
// idle daemon reports zeros and a screenshot of zeros teaches nothing. The
// incidents body is a 0.20.0 daemon's `api/incidents` after five Alertmanager
// deliveries, read with its read key, and its stamps are slid forward to the
// present as it is served: the screen prints every time as an age, so a body
// captured last quarter would otherwise put "3 months ago" on an OOM kill and
// read as a broken screen. One delta over every millisecond field, so the
// windows, the frozen findings and the before-or-after-the-restart reading all
// keep the distances the daemon measured.
//
// Usage: node fake-daemon.js <port> <busy|calm>
const http = require("node:http");
const { readFileSync } = require("node:fs");
const { join } = require("node:path");

const [port, variant = "busy"] = process.argv.slice(2);
if (!port) {
  console.error("usage: node fake-daemon.js <port> <busy|calm>");
  process.exit(2);
}

const fixture = (name) =>
  readFileSync(join(__dirname, "fixtures", `${name}.json`), "utf8");

/**
 * Every epoch-millisecond field of the incidents body, shifted by one delta so
 * the newest incident reads as two minutes old whenever the harness runs.
 */
const MS_FIELDS = new Set([
  "at_ms", "ended_at_ms", "window_from_ms", "window_to_ms", "oldest_finding_ms",
  "stored_at_ms", "first_seen_ms",
]);

function slideToNow(body) {
  const incidents = JSON.parse(body);
  const newest = Math.max(...incidents.map((incident) => incident.at_ms));
  const delta = Date.now() - 120_000 - newest;
  const slide = (node) => {
    if (Array.isArray(node)) return node.map(slide);
    if (node === null || typeof node !== "object") return node;
    return Object.fromEntries(Object.entries(node).map(
      ([key, value]) => [key, MS_FIELDS.has(key) && typeof value === "number" ? value + delta : slide(value)]));
  };
  return JSON.stringify(incidents.map(slide));
}

const routes = {
  "/api/status": fixture(`daemon-status-${variant}`),
  "/api/config": fixture("daemon-config"),
  "/api/export/report": fixture("daemon-report"),
  "/api/findings": fixture("daemon-findings"),
  "/api/incidents": slideToNow(fixture(`daemon-incidents-${variant}`)),
};

http
  .createServer((req, res) => {
    const path = req.url.split("?")[0].replace(/\/$/, "");
    const body = routes[path];
    if (body === undefined) {
      res.writeHead(404, { "content-type": "application/json" });
      res.end('{"error":"not found"}');
      return;
    }
    res.writeHead(200, { "content-type": "application/json" });
    res.end(body);
  })
  .listen(Number(port), "127.0.0.1", () =>
    console.log(`fake daemon (${variant}) on http://127.0.0.1:${port}`),
  );
