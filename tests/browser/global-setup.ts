import { spawn, spawnSync, ChildProcess } from "node:child_process";
import { existsSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { tmpdir } from "node:os";

// The Hub has no seeder, no fixture loader and no demo mode. Its validation
// refuses to start without a source, and a daemon view is read live rather
// than from storage, so the only way to a populated screen is a Hub that is
// really running against daemons that really answer.
//
// So this stands up: two fake daemons replaying real captures, a Hub built
// from this checkout, and four analysis runs submitted through the API to give
// the runs screen more than one state to show.

const ROOT = resolve(__dirname, "..", "..");
const WORK = join(tmpdir(), "perf-sentinel-hub-demo");
const HUB_PORT = 41500;
const BUSY_PORT = 41400;
const CALM_PORT = 41401;
// Nothing listens here, on purpose: a trace backend is never polled, so an
// unreachable row needs a daemon.
const DEAD_PORT = 41403;
const BASE = `http://127.0.0.1:${HUB_PORT}`;

// global.json pins the SDK with rollForward: disable, and it is usually not
// the dotnet on PATH.
const PINNED_SDK = "/usr/local/share/dotnet";
// The pinned SDK is reached by naming its binary outright rather than by putting
// its directory in front of PATH. Prepending would decide which `dotnet` runs by
// search order, and any writable directory later on that PATH could shadow it.
const DOTNET = existsSync(join(PINNED_SDK, "dotnet")) ? join(PINNED_SDK, "dotnet") : "dotnet";
const dotnetEnv = () => ({
  ...process.env,
  ...(existsSync(PINNED_SDK) ? { DOTNET_ROOT: PINNED_SDK } : {})
});

function engineBinary(): string {
  const fromEnv = process.env.HUB_ENGINE_BINARY;
  if (fromEnv) return fromEnv;
  const sibling = resolve(ROOT, "..", "..", "RustroverProjects", "perf-sentinel",
                          "target", "release", "perf-sentinel");
  if (existsSync(sibling)) return sibling;
  throw new Error(
    "No perf-sentinel binary. Three of the five screens need one, because the " +
    "Hub answers 503 to POST /api/analyses without it. Set HUB_ENGINE_BINARY, " +
    "or build the engine with `cargo build --release`.");
}

async function waitFor(url: string, seconds: number): Promise<void> {
  const deadline = Date.now() + seconds * 1000;
  while (Date.now() < deadline) {
    try {
      if ((await fetch(url)).ok) return;
    } catch {
      // not up yet
    }
    await new Promise((r) => setTimeout(r, 250));
  }
  throw new Error(`${url} never came up`);
}

async function submit(sourceId: string, request: unknown): Promise<string> {
  const response = await fetch(`${BASE}/api/analyses`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ source_id: sourceId, request })
  });
  const body = await response.json() as { id?: string };
  if (!body.id) throw new Error(`submit ${sourceId}: ${JSON.stringify(body)}`);
  return body.id;
}

async function settle(id: string, seconds: number): Promise<string> {
  const deadline = Date.now() + seconds * 1000;
  while (Date.now() < deadline) {
    const run = await (await fetch(`${BASE}/api/analyses/${id}`)).json() as { status: string };
    if (run.status !== "pending" && run.status !== "running") return run.status;
    await new Promise((r) => setTimeout(r, 300));
  }
  throw new Error(`run ${id} never reached a terminal state`);
}

// The same fixed epoch demo/capture-fixtures.sh pins. Observations are then
// rebased onto it and rounded to the second, which is as reproducible as a live
// capture gets: run ids are renumbered below, but how long the four runs took
// still varies between captures, so the file moves when that moves.
const EPOCH_MS = 1_756_512_000_000;
const SECOND_MS = 1_000;
// Above this a number is a moment, below it a duration. window_duration_ms and
// its neighbours are durations and must survive untouched.
const IS_TIMESTAMP = 1_000_000_000_000;

function rebase(value: unknown, capturedAt: number): unknown {
  if (Array.isArray(value)) return value.map((v) => rebase(v, capturedAt));
  if (value && typeof value === "object") {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value)) {
      out[k] = k.endsWith("_ms") && typeof v === "number" && v > IS_TIMESTAMP
        ? Math.round((EPOCH_MS - (capturedAt - v)) / SECOND_MS) * SECOND_MS
        : rebase(v, capturedAt);
    }
    return out;
  }
  return value;
}

// Records what the Hub answers on the five routes the launcher reads, so the
// site can serve a populated launcher with no Hub behind it. Captured here
// rather than in a script of its own because this is the only place a populated
// Hub exists, and capturing beside the screenshots keeps the two in step.
async function captureEmbedFixtures(runs: string[]): Promise<void> {
  const capturedAt = Date.now();
  const routes: Record<string, unknown> = {};
  // A run id is drawn from RandomNumberGenerator, so left alone it would rewrite
  // this file on every capture. Renumbered in order, the fixture only moves when
  // the data does. The replacements stay 16 hex characters because the launcher
  // routes #/run/{id} on exactly that shape.
  const stable = new Map(runs.map((id, i) => [id, (i + 1).toString(16).padStart(16, "0")]));
  const renumber = (text: string) =>
    [...stable].reduce((acc, [from, to]) => acc.split(from).join(to), text);
  const read = async (path: string) => {
    const response = await fetch(BASE + path);
    if (!response.ok) throw new Error(`${path} answered ${response.status}`);
    const body = rebase(await response.json(), capturedAt);
    routes[renumber(path)] = JSON.parse(renumber(JSON.stringify(body)));
  };

  await read("/api/status");
  await read("/api/sources");
  await read("/api/analyses?limit=500");
  for (const id of runs) await read(`/api/analyses/${id}`);
  // Only a daemon has a view. A trace backend answers 400, which is correct and
  // is not something the embed needs to replay.
  for (const id of ["checkout-prod", "billing-stg", "search-prod"]) {
    await read(`/api/sources/${id}/daemon`);
  }

  writeFileSync(join(__dirname, "demo", "fixtures", "hub-embed.json"),
                JSON.stringify({ epoch_ms: EPOCH_MS, routes }, null, 2) + "\n");
}

export default async function globalSetup(): Promise<void> {
  const engine = engineBinary();
  rmSync(WORK, { recursive: true, force: true });
  mkdirSync(join(WORK, "reports"), { recursive: true });

  const children: ChildProcess[] = [];
  // Detached like the Hub: an interrupted run must not leave these holding their
  // ports, or the next run's Hub binds against stale daemons and the capture
  // reads the previous run's data.
  const daemon = (port: number, variant: string) =>
    children.push(spawn(process.execPath, [join(__dirname, "demo", "fake-daemon.js"),
                                           String(port), variant],
                        { stdio: "ignore", detached: true }));
  daemon(BUSY_PORT, "busy");
  daemon(CALM_PORT, "calm");
  await waitFor(`http://127.0.0.1:${BUSY_PORT}/api/status`, 15);
  await waitFor(`http://127.0.0.1:${CALM_PORT}/api/status`, 15);

  const build = spawnSync(DOTNET,
    ["build", join(ROOT, "PerfSentinelHub", "PerfSentinelHub.csproj"), "-c", "Release", "--nologo"],
    { env: dotnetEnv(), stdio: "inherit" });
  if (build.status !== 0) throw new Error("the Hub did not build");

  const source = (i: number, id: string, name: string, environment: string,
                  kind: string, port: number, extra: Record<string, string> = {}) => {
    const out: Record<string, string> = {
      [`Hub__Sources__${i}__Id`]: id,
      [`Hub__Sources__${i}__Name`]: name,
      [`Hub__Sources__${i}__Environment`]: environment,
      [`Hub__Sources__${i}__Kind`]: kind,
      [`Hub__Sources__${i}__BaseUrl`]: `http://127.0.0.1:${port}`
    };
    for (const [k, v] of Object.entries(extra)) out[`Hub__Sources__${i}__${k}`] = v;
    return out;
  };

  // The built binary rather than `dotnet run`: the latter spawns the app as a
  // grandchild that outlives a SIGTERM to its parent, and a Hub left holding
  // the port makes the next run read the previous run's data.
  // `dotnet run` rather than the built binary: the build output holds a static
  // web asset manifest, not wwwroot itself, so the binary alone serves the API
  // and no page. Detached, because dotnet run puts the Hub in a grandchild that
  // survives a signal to its parent, and a Hub left holding the port makes the
  // next run read the previous run's data. Teardown signals the whole group.
  const hub = spawn(DOTNET,
    ["run", "--project", join(ROOT, "PerfSentinelHub", "PerfSentinelHub.csproj"),
     "-c", "Release", "--no-build", "--no-launch-profile"],
    {
      stdio: "ignore",
      detached: true,
      env: {
        ...dotnetEnv(),
        ASPNETCORE_URLS: BASE,
        ASPNETCORE_ENVIRONMENT: "Production",
        Hub__DatabasePath: join(WORK, "hub.db"),
        // No screenshot should depend on GitHub being reachable.
        Hub__UpdateCheck__Enabled: "false",
        Hub__Analysis__EngineBinaryPath: engine,
        Hub__Analysis__ReportDirectory: join(WORK, "reports"),
        ...source(0, "checkout-prod", "Checkout production", "production", "daemon", BUSY_PORT),
        ...source(1, "billing-stg", "Billing staging", "staging", "daemon", CALM_PORT),
        ...source(2, "tempo-eu", "Tempo EU", "production", "tempo", 41402,
                  { RetentionHours: "168" }),
        ...source(3, "search-prod", "Search production", "production", "daemon", DEAD_PORT),
        // Victoria Traces speaks the Jaeger query API, which is the kind the
        // engine reads it with. Two backends rather than one so the group the
        // list draws holds more than a single row.
        ...source(4, "victoria-eu", "Victoria Traces EU", "staging", "jaeger_query", 41404,
                  { RetentionHours: "72" })
      }
    });
  children.push(hub);
  await waitFor(`${BASE}/health/ready`, 90);

  // Four runs, chosen for the states they end in rather than for variety:
  // two that succeed, one refused by a dead source, one refused before it is
  // ever queued because the window outruns what the backend keeps.
  const succeeded = await submit("checkout-prod", {});
  const alsoSucceeded = await submit("billing-stg", {});
  const unreachable = await submit("search-prod", {});
  const refused = await submit("tempo-eu",
    { service: "checkout", lookback: "999h", max_traces: 100 });
  for (const id of [succeeded, alsoSucceeded, unreachable, refused]) await settle(id, 120);

  try {
    await captureEmbedFixtures([succeeded, alsoSucceeded, unreachable, refused]);
  } catch (error) {
    // The embed fixture is consumed by the website repository, not by this
    // suite. A capture that fails leaves the previous one in place and must not
    // stop the screenshots this suite exists to produce.
    console.error("hub-embed.json was not refreshed:", error);
  }

  writeFileSync(join(WORK, "state.json"), JSON.stringify({
    baseUrl: BASE,
    succeeded,
    pids: children.map((c) => c.pid).filter((p): p is number => typeof p === "number"),
    hubGroup: hub.pid
  }, null, 2));
}

export { WORK };
