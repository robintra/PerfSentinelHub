// Replays a real daemon's five responses so the launcher has something to draw.
//
// The bodies in fixtures/ are captures from a real 0.16.0 daemon, not
// hand-written shapes: the findings come from `analyze --format json` on the
// engine's own demo fixture, and the config is that daemon's `api/config`
// verbatim. Only the gauge values in the status files are chosen, because an
// idle daemon reports zeros and a screenshot of zeros teaches nothing. The
// incidents body is a 0.20.0 daemon's `api/incidents` after one Alertmanager
// delivery, read with its read key.
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

const routes = {
  "/api/status": fixture(`daemon-status-${variant}`),
  "/api/config": fixture("daemon-config"),
  "/api/export/report": fixture("daemon-report"),
  "/api/findings": fixture("daemon-findings"),
  "/api/incidents": fixture("daemon-incidents"),
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
