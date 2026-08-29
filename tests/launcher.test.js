// The launcher's pure command builders. Node's own runner, no dependency and
// no build step, mirroring how launcher.js itself is authored.
//
// Only the quoting and the command shapes are covered: they are the one piece
// of non-trivial logic on the page, and a wrong quote would print a command
// that runs as something other than what it says.
const test = require("node:test");
const assert = require("node:assert");

globalThis.window = globalThis;
require("../PerfSentinelHub/wwwroot/launcher.js");
const PSL = globalThis.PSL;

const tempo = {
  kind: "tempo",
  engine_subcommand: "tempo",
  base_url: "http://tempo.obs.svc:3200",
  auth_header_name: null
};

test("a plain value is left unquoted", () => {
  assert.equal(PSL.shq("order-service"), "order-service");
  assert.equal(PSL.shq("ns/svc:v1.2"), "ns/svc:v1.2");
});

test("a shell expansion is quoted into a literal", () => {
  // Double quotes would still expand this. Single quotes are the whole point.
  assert.equal(PSL.shq("$(whoami)"), "'$(whoami)'");
  assert.equal(PSL.shq("a`b`c"), "'a`b`c'");
});

test("an apostrophe closes and reopens rather than escaping in place", () => {
  // A backslash escapes nothing inside single quotes.
  assert.equal(PSL.shq("o'reilly svc"), "'o'\\''reilly svc'");
});

test("an empty value is quoted rather than dropped", () => {
  assert.equal(PSL.shq(""), "''");
});

test("a relative window becomes one lookback and a cap", () => {
  assert.equal(
    PSL.analysisCommand(tempo, { service: "order-service", max_traces: 100, lookback: "1h" }),
    "perf-sentinel tempo --endpoint http://tempo.obs.svc:3200 --service order-service \\\n"
      + "  --lookback 1h --max-traces 100");
});

test("an absolute window is whole seconds, the way the Hub writes them", () => {
  const command = PSL.analysisCommand(
    tempo,
    { service: "orders", max_traces: 250, from_ms: 1787835540600, to_ms: 1787838540400 });

  assert.match(command, /--from 2026-08-27T12:59:00Z --to 2026-08-27T13:49:00Z/);
  // The engine refuses a lookback beside an absolute window.
  assert.ok(!command.includes("--lookback"));
});

test("a trace id carries neither a window nor a cap", () => {
  const command = PSL.analysisCommand(tempo, { trace_id: "abc123def456" });

  assert.equal(
    command,
    "perf-sentinel tempo --endpoint http://tempo.obs.svc:3200 --trace-id abc123def456");
  assert.ok(!command.includes("--max-traces"));
});

test("an authenticated source reads its header from the environment", () => {
  const command = PSL.analysisCommand(
    { ...tempo, auth_header_name: "Authorization" },
    { service: "orders", max_traces: 10, lookback: "2h" });

  assert.ok(command.includes("--auth-header-env PERF_SENTINEL_SOURCE_TOKEN"));
  // The value never reaches the page, so it cannot reach the command.
  assert.ok(!command.includes("Bearer"));
});

test("changed thresholds point the engine at a file, having no flag of their own", () => {
  const command = PSL.analysisCommand(
    tempo,
    { service: "orders", max_traces: 10, lookback: "1h", detection: { max_fanout: 9 } });

  assert.ok(command.includes("-c .perf-sentinel.toml"));
  assert.equal(PSL.detectionToml({ max_fanout: 9, n_plus_one_min_occurrences: 8 }),
    "[detection]\nmax_fanout = 9\nn_plus_one_min_occurrences = 8");
});

test("a daemon has no command at all", () => {
  assert.equal(PSL.analysisCommand({ engine_subcommand: null, base_url: "http://d:4318" }, {}), null);
});

test("the monitor keeps --daemon on query, where the engine puts it", () => {
  assert.equal(
    PSL.monitorCommand({ base_url: "http://daemon.obs.svc:4318" }),
    "perf-sentinel query --daemon http://daemon.obs.svc:4318 monitor");
});

test("the shell note fires only when something was actually quoted", () => {
  assert.ok(!PSL.quotedForShell(PSL.analysisCommand(tempo, { service: "orders", max_traces: 1, lookback: "1h" })));
  assert.ok(PSL.quotedForShell(PSL.analysisCommand(tempo, { service: "two words", max_traces: 1, lookback: "1h" })));
});

test("a light refresh classifies from its gauges plus the kept hints", () => {
  // Mirrors DaemonView.Classify: a status-only body has no hints of its own.
  const at = { value: 90, capacity: 100, pct: 90, at_capacity: true };
  const under = { value: 1, capacity: 100, pct: 1, at_capacity: false };
  const unknown = { value: 1, capacity: null, pct: null, at_capacity: false };

  assert.equal(PSL.lightState({ traces: at, analysis_queue: under, findings: under }, 0), "near_capacity");
  // The full gauge outranks the hints, exactly as the server rules it.
  assert.equal(PSL.lightState({ traces: at, analysis_queue: under, findings: under }, 3), "near_capacity");
  assert.equal(PSL.lightState({ traces: under, analysis_queue: under, findings: under }, 2), "advised");
  assert.equal(PSL.lightState({ traces: unknown, analysis_queue: unknown, findings: unknown }, 0), "unknown");
  assert.equal(PSL.lightState({ traces: under, analysis_queue: unknown, findings: under }, 0), "ok");
});
