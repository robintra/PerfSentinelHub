// The launcher against the real engine. Node's own runner, no package and no
// build step, the way launcher.js itself is authored.
//
// launcher.test.js covers the builders in isolation, which cannot catch a
// command that is well-formed and wrong: a config file the engine refuses, a
// threshold name it does not know, a file named something the run never reads.
// So this file hands what the page prints to the binary the page describes and
// lets the engine answer. It needs a built engine, so it skips cleanly when
// there is none and the Hub's own CI never requires one. The table tests at
// the end are the exception and always run: they read the C# tables as text,
// which needs nothing built.
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

// Each knob at a value no default holds, so a file that carried only its
// section header would leave every assertion below unmet. Writing the
// defaults instead proved nothing: the engine's own config already equals
// them, so an empty file passed.
//
// The left name is the file key the launcher writes, the right one is what
// the engine calls it back in detection_config. Four of the eight differ,
// and that pairing is duplicated in DaemonDefaults.ExportSpelling and in
// app.js DETECT_ALIAS, so the run below and the table tests after it are what
// keep the three in step.
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

test("every threshold the Hub can write reaches the engine under its own name", { skip }, () => {
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

// ------------------------------------------------- the tables the JS copies
//
// The knob table lives in C#, and the map above and DETECTION_COPY in app.js
// are both copies of its names typed by hand. Nothing binds the three, so a
// rename or a changed default in DetectionOverrides.cs leaves every test here
// green while the page offers a knob the Hub now refuses. The tests below read
// the C# as text and hold the JS to it, with no engine and no build.
//
// Reading C# with a regex is crude, so every read asserts how many entries it
// found and every unresolved token fails: a table that changed shape fails
// loudly here rather than quietly matching nothing.
const analysisDir = path.join(__dirname, "..", "PerfSentinelHub", "Analysis");
const overridesSource = path.join(analysisDir, "DetectionOverrides.cs");
const defaultsSource = path.join(analysisDir, "DaemonDefaults.cs");
const appSource = path.join(__dirname, "..", "PerfSentinelHub", "wwwroot", "app.js");
const KNOB_COUNT = 8;
const ALIAS_COUNT = 4;

/** The body of one table, from its declaration to the first closer after it. */
function tableIn(file, declaration, closer) {
  const source = fs.readFileSync(file, "utf8");
  const opened = source.indexOf(declaration);
  assert.notEqual(opened, -1, path.basename(file) + " no longer declares " + declaration);
  const closed = source.indexOf(closer, opened);
  assert.notEqual(closed, -1, declaration + " in " + path.basename(file) + " has no " + closer);
  return source.slice(opened + declaration.length, closed);
}

/** The entries of a table, refusing any count but the one expected. */
function entriesIn(body, pattern, expected, what) {
  const found = Array.from(body.matchAll(pattern));
  assert.equal(found.length, expected, "read " + found.length + " " + what + ", expected " + expected);
  return found;
}

/** One field of a knob tuple, written either as a literal or as a const. */
function numberIn(token, constants, knob) {
  const digits = token.replace(/_/g, "");
  if (/^\d+$/.test(digits)) return Number(digits);
  assert.ok(Object.hasOwn(constants, token),
    knob + " is bounded by " + token + ", which DetectionOverrides.cs declares as no const int");
  return constants[token];
}

/** Wire name to bounds and engine default, read out of DetectionOverrides.Knobs. */
function declaredKnobs() {
  const source = fs.readFileSync(overridesSource, "utf8");
  const constants = {};
  Array.from(source.matchAll(/private const int (\w+) = ([\d_]+);/g)).forEach(function (found) {
    constants[found[1]] = Number(found[2].replace(/_/g, ""));
  });

  const knobs = {};
  const table = tableIn(overridesSource, "Knobs =", "];");
  entriesIn(table, /\("([a-z0-9_]+)", (\w+), (\w+), (\w+)\)/g, KNOB_COUNT, "knobs in DetectionOverrides.cs")
    .forEach(function (found) {
      knobs[found[1]] = {
        min: numberIn(found[2], constants, found[1]),
        max: numberIn(found[3], constants, found[1]),
        default: numberIn(found[4], constants, found[1])
      };
    });
  return knobs;
}

test("the knobs this file and app.js name are the ones DetectionOverrides.cs declares", () => {
  // Compared sorted, since neither JS copy owes the C# its order. The counts
  // are what a knob added on one side only runs into.
  const declared = Object.keys(declaredKnobs()).sort();
  assert.deepEqual(Object.keys(written).sort(), declared,
    "the map above and DetectionOverrides.Knobs name different knobs");

  const copy = entriesIn(
    tableIn(appSource, "const DETECTION_COPY = {", "};"),
    /^\s+([a-z0-9_]+): "/gm,
    KNOB_COUNT,
    "sentences in app.js DETECTION_COPY"
  ).map(function (found) { return found[1]; });
  assert.deepEqual(copy.sort(), declared,
    "app.js DETECTION_COPY is keyed on knobs DetectionOverrides.cs does not declare");
});

test("the values this file writes are still off the defaults DetectionOverrides.cs holds", () => {
  const knobs = declaredKnobs();
  Object.keys(written).forEach(function (key) {
    const knob = knobs[key];
    assert.ok(knob, key + " is no knob DetectionOverrides.cs declares");
    const value = written[key][0];
    // A value equal to the default is dropped before the file is written, so a
    // default that moved onto one of these would empty the [detection] section
    // and leave the run above asserting nothing.
    assert.notEqual(value, knob.default, key + " is written at its own default of " + knob.default);
    assert.ok(value >= knob.min && value <= knob.max,
      key + " is written at " + value + ", outside the " + knob.min + " to " + knob.max + " the Hub accepts");
  });
  // The fixture holds six near-identical queries, so the default the first run
  // leans on has to stay under that count for the untouched report to hold one.
  assert.ok(knobs.n_plus_one_min_occurrences.default <= 6,
    "the default rose past the six the fixture holds");
});

test("the four alias pairs read the same in DaemonDefaults.cs, in app.js and here", () => {
  const declared = {};
  entriesIn(
    tableIn(defaultsSource, "ExportSpelling = new(StringComparer.Ordinal)", "};"),
    /\["([a-z0-9_]+)"\] = "([a-z0-9_]+)"/g,
    ALIAS_COUNT,
    "pairs in DaemonDefaults.ExportSpelling"
  ).forEach(function (found) { declared[found[1]] = found[2]; });

  // app.js reads a pair from the export side, so it is keyed the other way.
  const page = {};
  entriesIn(
    tableIn(appSource, "const DETECT_ALIAS = {", "};"),
    /([a-z0-9_]+): "([a-z0-9_]+)"/g,
    ALIAS_COUNT,
    "pairs in app.js DETECT_ALIAS"
  ).forEach(function (found) { page[found[2]] = found[1]; });
  assert.deepEqual(page, declared, "app.js DETECT_ALIAS and DaemonDefaults.ExportSpelling disagree");

  // The same four out of the map above, which is the third copy of them.
  const here = {};
  Object.keys(written).forEach(function (key) {
    if (written[key][1] !== key) here[key] = written[key][1];
  });
  assert.deepEqual(here, declared,
    "the map above renames other knobs than DaemonDefaults.ExportSpelling does");
});
