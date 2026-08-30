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

  // Undotted: a downloaded file may not keep a leading dot, so the name the
  // command asks for is the one the reader is most likely to actually have.
  assert.ok(command.includes("-c perf-sentinel.toml"));
  assert.ok(!command.includes("-c .perf-sentinel.toml"));
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

test("a light refresh only merges onto a view that carries the rest", () => {
  const full = { error_code: null, warnings: [], config: { api_enabled: true }, state: "ok" };

  assert.equal(PSL.mergeableView(undefined), null);
  assert.equal(PSL.mergeableView("loading"), null);
  assert.equal(PSL.mergeableView({ error_code: "source_unreachable" }), null);
  // A light body has no warnings of its own, so it is never a base for another.
  assert.equal(PSL.mergeableView({ error_code: null, traces: null }), null);
  assert.equal(PSL.mergeableView(full), full);
});

test("a merged light view keeps the settings and takes the gauges", () => {
  const under = { value: 1, capacity: 100, pct: 1, at_capacity: false };
  const at = { value: 99, capacity: 100, pct: 99, at_capacity: true };
  const previous = {
    error_code: null, observed_at_ms: 1000, version: "0.16.0", uptime_seconds: 10,
    warnings: [{ kind: "tuning", message: "m" }], warnings_dropped: 0,
    config: { api_enabled: true }, state: "advised",
    traces: under, analysis_queue: under, findings: under
  };
  const light = {
    observed_at_ms: 2000, version: "0.16.0", uptime_seconds: 70,
    traces: at, analysis_queue: under, findings: under
  };

  const merged = PSL.mergeLight(previous, light);

  assert.equal(merged.observed_at_ms, 2000);
  assert.equal(merged.uptime_seconds, 70);
  assert.deepEqual(merged.traces, at);
  // What a light read cannot see is the last full read's, unchanged.
  assert.deepEqual(merged.config, previous.config);
  assert.deepEqual(merged.warnings, previous.warnings);
  // And the state follows the new gauges, not the state that came with them.
  assert.equal(merged.state, "near_capacity");
  // The previous view is left alone: the panel may still be rendering it.
  assert.equal(previous.state, "advised");
  assert.deepEqual(previous.traces, under);
});

test("what the next re-read of a row should cost", () => {
  const full = { error_code: null, warnings: [] };
  const failed = { error_code: "network_error" };

  // Nothing on screen yet, or a read in flight: only a full read renders.
  assert.equal(PSL.refreshPlan(undefined, 0, 60000), "full");
  assert.equal(PSL.refreshPlan("loading", 0, 60000), "full");
  // A row that failed asks the cheap question until something answers.
  assert.equal(PSL.refreshPlan(failed, 0, 60000), "probe");
  assert.equal(PSL.refreshPlan(failed, 999999, 60000), "probe");
  // A row already showing a daemon rides on light reads until the full one is due.
  assert.equal(PSL.refreshPlan(full, 5000, 60000), "light");
  assert.equal(PSL.refreshPlan(full, 60000, 60000), "full");
  assert.equal(PSL.refreshPlan(full, 60001, 60000), "full");
  // A light body is never a base for another, so it reads in full instead.
  assert.equal(PSL.refreshPlan({ error_code: null, traces: null }, 0, 60000), "full");
});

test("the download link points at the engine version the Hub runs", () => {
  assert.equal(PSL.releaseUrl("0.16.0"),
    "https://github.com/robintra/perf-sentinel/releases/tag/v0.16.0");
  assert.equal(PSL.releaseUrl("0.17.0-rc.1"),
    "https://github.com/robintra/perf-sentinel/releases/tag/v0.17.0-rc.1");
  // No version to pin, so the release list rather than a made-up tag.
  const list = "https://github.com/robintra/perf-sentinel/releases";
  assert.equal(PSL.releaseUrl(null), list);
  assert.equal(PSL.releaseUrl(""), list);
  assert.equal(PSL.releaseUrl("unknown"), list);
  // And nothing that is not a version is ever pasted into the path.
  assert.equal(PSL.releaseUrl("0.16.0/../../evil"), list);
  assert.equal(PSL.releaseUrl("0.16.0?x=1"), list);
  assert.equal(PSL.releaseUrl("javascript:alert(1)"), list);
});

test("the monitor command carries the interval the row is re-reading on", () => {
  const source = { id: "d", base_url: "http://daemon.svc:4318" };
  const bare = "perf-sentinel query --daemon http://daemon.svc:4318 monitor";

  assert.equal(PSL.monitorCommand(source, 30), bare + " --refresh 30");
  assert.equal(PSL.monitorCommand(source, 5), bare + " --refresh 5");
  // Not re-reading, so no interval to mirror: the engine keeps its own default.
  assert.equal(PSL.monitorCommand(source, 0), bare);
  assert.equal(PSL.monitorCommand(source), bare);
  // --daemon is the parent's, --refresh is the subcommand's, in that order.
  assert.match(PSL.monitorCommand(source, 10), /--daemon \S+ monitor --refresh 10$/);
  // And an address that needs quoting still gets it.
  assert.equal(PSL.monitorCommand({ base_url: "http://a b" }, 5),
    "perf-sentinel query --daemon 'http://a b' monitor --refresh 5");
});

test("only open folds are worth remembering", () => {
  assert.deepEqual(PSL.openFolds({ a: true, b: false, c: true }), { a: true, c: true });
  // Closed is the default, so nothing closed is written down.
  assert.deepEqual(PSL.openFolds({ a: false }), {});
  assert.deepEqual(PSL.openFolds({}), {});
  assert.deepEqual(PSL.openFolds(null), {});
  assert.deepEqual(PSL.openFolds(undefined), {});
  // And nothing that is not exactly true counts as open.
  assert.deepEqual(PSL.openFolds({ a: "true", b: 1, c: {}, d: true }), { d: true });
});

test("a gauge takes a tone only once it is close to a cap it published", () => {
  // 90 is the engine's own advisor line, the one that turns the row's verdict.
  assert.equal(PSL.gaugeTone(90), "crit");
  assert.equal(PSL.gaugeTone(100), "crit");
  assert.equal(PSL.gaugeTone(89.9), "warn");
  assert.equal(PSL.gaugeTone(75), "warn");
  assert.equal(PSL.gaugeTone(74.9), null);
  assert.equal(PSL.gaugeTone(0), null);
  // No published cap, so nothing is known about how close to one it is.
  assert.equal(PSL.gaugeTone(null), null);
  assert.equal(PSL.gaugeTone(undefined), null);
  assert.equal(PSL.gaugeTone(NaN), null);
  assert.equal(PSL.gaugeTone("90"), null);
});

test("a gauge move is what changed, and nothing when nothing did", () => {
  const at = value => ({ value, capacity: 10000 });

  assert.equal(PSL.gaugeMove(at(8057), at(9310)), 1253);
  assert.equal(PSL.gaugeMove(at(9310), at(9251)), -59);
  // Unchanged, unknown either side, or no earlier reading at all.
  assert.equal(PSL.gaugeMove(at(9310), at(9310)), null);
  assert.equal(PSL.gaugeMove(null, at(9310)), null);
  assert.equal(PSL.gaugeMove(at(9310), null), null);
  assert.equal(PSL.gaugeMove({ value: null }, at(9310)), null);
  assert.equal(PSL.gaugeMove(at(9310), { value: null }), null);
  // Zero is a real reading, not a missing one.
  assert.equal(PSL.gaugeMove(at(0), at(12)), 12);
  assert.equal(PSL.gaugeMove(at(12), at(0)), -12);
});

test("PowerShell quotes by doubling, and keeps its own operators out of bare words", () => {
  // Inside single quotes everything is literal and a quote is doubled, where a
  // POSIX shell has to close, escape and reopen.
  assert.equal(PSL.psq("a b'c"), "'a b''c'");
  assert.equal(PSL.shq("a b'c"), "'a b'\\''c'");
  // A comma is PowerShell's array operator and @ opens a splat, so neither
  // stays bare even though a POSIX shell would leave them alone.
  assert.equal(PSL.psq("a,b"), "'a,b'");
  assert.equal(PSL.psq("@thing"), "'@thing'");
  assert.equal(PSL.shq("a,b"), "a,b");
  // What both leave alone: a URL, a name, a number, an ISO timestamp.
  assert.equal(PSL.psq("http://tempo.svc:3200"), "http://tempo.svc:3200");
  assert.equal(PSL.psq("order-service"), "order-service");
  // Nothing in an ISO timestamp is special to either shell, so both pass it bare.
  assert.equal(PSL.psq("2026-08-29T09:00:00.000Z"), "2026-08-29T09:00:00.000Z");
  assert.equal(PSL.shq("2026-08-29T09:00:00.000Z"), "2026-08-29T09:00:00.000Z");
  // And the empty string is quoted by both, or it would vanish.
  assert.equal(PSL.psq(""), "''");
});

test("the shell a first visit gets follows the platform", () => {
  assert.equal(PSL.defaultShell("Win32"), "powershell");
  assert.equal(PSL.defaultShell("Windows"), "powershell");
  assert.equal(PSL.defaultShell("MacIntel"), "posix");
  assert.equal(PSL.defaultShell("Linux x86_64"), "posix");
  // Nothing to go on is not Windows, so it is the line most machines run.
  // "Darwin" contains "win", so the test has to be anchored.
  assert.equal(PSL.defaultShell("Darwin"), "posix");
  assert.equal(PSL.defaultShell("darwin"), "posix");
  assert.equal(PSL.defaultShell(null), "posix");
  assert.equal(PSL.defaultShell(""), "posix");
  assert.equal(PSL.shellById("nonsense").id, "posix");
  assert.equal(PSL.shellById("powershell").label, "PowerShell");
});

test("a command continues its line the way its own shell does", () => {
  const source = { engine_subcommand: "tempo", base_url: "http://tempo.svc:3200" };
  const request = { service: "orders", lookback: "1h", max_traces: 100, detection: {} };

  const posix = PSL.analysisCommand(source, request, "posix");
  const pwsh = PSL.analysisCommand(source, request, "powershell");
  assert.match(posix, /\\\n {2}--lookback/);
  assert.match(pwsh, /`\n {2}--lookback/);
  // The backslash never appears in the PowerShell line, and vice versa.
  assert.ok(!pwsh.includes("\\"));
  assert.ok(!posix.includes("`"));
  // The monitor command takes its shell too.
  assert.equal(PSL.monitorCommand({ base_url: "http://a b" }, 5, "powershell"),
    "perf-sentinel query --daemon 'http://a b' monitor --refresh 5");
});

test("setting an environment variable is written the way each shell writes it", () => {
  const header = "Authorization: …";
  assert.equal(PSL.exportLine("posix", "PERF_SENTINEL_SOURCE_TOKEN", header),
    "export PERF_SENTINEL_SOURCE_TOKEN='Authorization: …'");
  // PowerShell assigns into the env: drive, and wants the spaces.
  assert.equal(PSL.exportLine("powershell", "PERF_SENTINEL_SOURCE_TOKEN", header),
    "$env:PERF_SENTINEL_SOURCE_TOKEN = 'Authorization: …'");
  // A quote in the value is escaped by each shell's own rule.
  assert.equal(PSL.exportLine("posix", "V", "a'b"), "export V='a'\\''b'");
  assert.equal(PSL.exportLine("powershell", "V", "a'b"), "$env:V = 'a''b'");
  // An unknown shell is the POSIX one, as everywhere else.
  assert.match(PSL.exportLine("nonsense", "V", "x"), /^export V=/);
});

test("updateState only speaks when both versions are known and one is older", () => {
  // Nothing to say, and the three silences are different situations.
  assert.equal(PSL.updateState(null, "0.17.0"), null, "no version running");
  assert.equal(PSL.updateState("0.16.0", null), null, "check off or not run yet");
  assert.equal(PSL.updateState("0.17.0", "0.17.0"), null, "current");
  // A build ahead of the newest release is a pre-release, not a downgrade.
  assert.equal(PSL.updateState("0.18.0", "0.17.0"), null, "ahead");

  assert.deepEqual(PSL.updateState("0.16.0", "0.17.0"), { latest: "0.17.0" });
  assert.deepEqual(PSL.updateState("0.14.2", "0.17.0"), { latest: "0.17.0" });
  // The Hub's own version has a fourth segment that never carries meaning.
  assert.equal(PSL.updateState("0.1.0.0", "0.1.0"), null, "four segments, same release");
  assert.deepEqual(PSL.updateState("0.1.0.0", "0.2.0"), { latest: "0.2.0" });
});

test("hubReleaseUrl lands on the list, which exists before any release does", () => {
  assert.equal(PSL.hubReleaseUrl(), "https://github.com/robintra/PerfSentinelHub/releases");
});

test("knownShell answers null for anything that is not a shell id", () => {
  assert.equal(PSL.knownShell("posix"), "posix");
  assert.equal(PSL.knownShell("powershell"), "powershell");
  // shellById would answer "posix" for all of these, which is right for
  // spelling a command and wrong for judging a remembered value.
  assert.equal(PSL.knownShell("fish"), null);
  assert.equal(PSL.knownShell(""), null);
  assert.equal(PSL.knownShell(null), null);
  assert.equal(PSL.knownShell(undefined), null);
});

test("durPrecise always reaches the seconds a countdown is watched by", () => {
  // dur() stops at two units, which hides the only figure that moves.
  assert.equal(PSL.dur(86_399_000), "23 h 59 m");
  assert.equal(PSL.durPrecise(86_399_000), "23 h 59 m 59 s");
  assert.equal(PSL.durPrecise(90_061_000), "1 d 1 h 1 m 1 s");
  assert.equal(PSL.durPrecise(59_000), "59 s");
  assert.equal(PSL.durPrecise(0), "0 s");
  // A report already gone must not read as one second from expiry.
  assert.equal(PSL.durPrecise(-5_000), "0 s");
  assert.equal(PSL.durPrecise(null), "n/a");
});
