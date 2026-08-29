// The launcher against the real engine. Node's own runner, no package and no
// build step, the way launcher.js itself is authored.
//
// launcher.test.js covers the builders in isolation, which cannot catch a
// command that is well-formed and wrong: a config file the engine refuses, a
// threshold name it does not know, a file named something the run never reads.
// So this file hands what the page prints to the binary the page describes and
// lets the engine answer. It needs a built engine, so it skips cleanly when
// there is none and the Hub's own CI never requires one.
const test = require("node:test");
const assert = require("node:assert");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

globalThis.window = globalThis;
require("../PerfSentinelHub/wwwroot/launcher.js");
const PSL = globalThis.PSL;

// The engine is its own checkout beside this one. Point PERF_SENTINEL_REPO at
// another one to run against a different build.
const engineRepo = process.env.PERF_SENTINEL_REPO
  || path.join(os.homedir(), "RustroverProjects", "perf-sentinel");
const engine = path.join(engineRepo, "target", "release", "perf-sentinel");
// Six near-identical queries in one trace, which is one N+1 at the default
// threshold of five and nothing at all above six.
const fixture = path.join(engineRepo, "tests", "fixtures", "n_plus_one_sql.json");

const skip = fs.existsSync(engine) && fs.existsSync(fixture)
  ? false
  : "no engine at " + engine + ": build one with cargo build --release, or set "
    + "PERF_SENTINEL_REPO to a perf-sentinel checkout that has been built";
if (skip) console.log("# SKIP " + skip);

// A source and a request of the shape app.js buildRequest posts for a service
// run over a relative window, which is what the form submits by default.
const source = {
  kind: "tempo",
  engine_subcommand: "tempo",
  base_url: "http://tempo.obs.svc:3200",
  auth_header_name: null
};
const request = { service: "order-svc", max_traces: 100, lookback: "1h" };

/** The config file name out of the printed command, never out of this test. */
function configNamedBy(detection) {
  const command = PSL.analysisCommand(source, Object.assign({ detection }, request));
  const named = command.match(/ -c (\S+)/);
  assert.ok(named, "the command carries no -c flag: " + command);
  return named[1];
}

/**
 * Runs the engine in a directory holding the file the command names, so the
 * relative name resolves exactly as it does for a reader who followed the
 * instructions and put the download beside them.
 */
function analyze(detection) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "psl-launcher-"));
  try {
    const args = ["analyze", "--input", fixture, "--format", "json"];
    if (detection) {
      const name = configNamedBy(detection);
      fs.writeFileSync(path.join(dir, name), PSL.detectionToml(detection));
      args.push("-c", name);
    }
    const run = spawnSync(engine, args, { cwd: dir, encoding: "utf8" });
    assert.equal(run.status, 0, "the engine refused the run: " + run.stderr);
    return JSON.parse(run.stdout);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
}

test("the engine reads the file the command names, and the threshold takes effect", { skip }, () => {
  // Above the six the fixture holds, so the detector stops seeing them.
  const tuned = analyze({ n_plus_one_min_occurrences: 20 });
  const untouched = analyze(null);

  // Both directions matter: a fixture that found nothing to begin with would
  // make any threshold look like it worked.
  assert.ok(untouched.findings.length > 0, "the fixture holds no finding to suppress");
  assert.equal(tuned.findings.length, 0);
  // And the engine read the value, rather than the file being parsed and ignored.
  assert.equal(tuned.detection_config.n_plus_one_threshold, 20);
});

test("every threshold the Hub can write reaches the engine under its own name", { skip }, () => {
  // Each knob at a value no default holds, so a file that carried only its
  // section header would leave every assertion below unmet. Writing the
  // defaults instead proved nothing: the engine's own config already equals
  // them, so an empty file passed.
  //
  // The left name is the file key the launcher writes, the right one is what
  // the engine calls it back in detection_config. Four of the eight differ,
  // and that pairing is duplicated in DaemonDefaults.ExportSpelling and in
  // app.js DETECT_ALIAS, so this run is what keeps the three in step.
  const written = {
    n_plus_one_min_occurrences: [21, "n_plus_one_threshold"],
    window_duration_ms: [1234, "window_ms"],
    slow_query_threshold_ms: [777, "slow_threshold_ms"],
    slow_query_min_occurrences: [9, "slow_min_occurrences"],
    max_fanout: [42, "max_fanout"],
    chatty_service_min_calls: [33, "chatty_service_min_calls"],
    pool_saturation_concurrent_threshold: [17, "pool_saturation_concurrent_threshold"],
    serialized_min_sequential: [8, "serialized_min_sequential"]
  };

  const detection = {};
  Object.keys(written).forEach(function (key) { detection[key] = written[key][0]; });
  // The engine refuses an unknown field in [detection] outright, so a knob
  // renamed on either side fails this run rather than silently reverting an
  // operator's choice.
  const applied = analyze(detection).detection_config;
  const untouched = analyze(null).detection_config;

  Object.keys(written).forEach(function (key) {
    const [value, engineName] = written[key];
    assert.equal(applied[engineName], value, key + " did not reach the engine as " + engineName);
    // Every value has to differ from the default, or the assertion above would
    // hold for a file the engine never read.
    assert.notEqual(untouched[engineName], value, key + " was written at its own default");
  });
  assert.equal(Object.keys(written).length, 8, "DetectionOverrides.Knobs holds eight");
});
