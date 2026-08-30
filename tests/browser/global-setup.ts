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
const dotnetEnv = () => ({
  ...process.env,
  PATH: existsSync(PINNED_SDK) ? `${PINNED_SDK}:${process.env.PATH}` : process.env.PATH,
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

export default async function globalSetup(): Promise<void> {
  const engine = engineBinary();
  rmSync(WORK, { recursive: true, force: true });
  mkdirSync(join(WORK, "reports"), { recursive: true });

  const children: ChildProcess[] = [];
  const daemon = (port: number, variant: string) =>
    children.push(spawn(process.execPath, [join(__dirname, "demo", "fake-daemon.js"),
                                           String(port), variant],
                        { stdio: "ignore" }));
  daemon(BUSY_PORT, "busy");
  daemon(CALM_PORT, "calm");
  await waitFor(`http://127.0.0.1:${BUSY_PORT}/api/status`, 15);
  await waitFor(`http://127.0.0.1:${CALM_PORT}/api/status`, 15);

  const build = spawnSync("dotnet",
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
  const hub = spawn("dotnet",
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
        ...source(3, "search-prod", "Search production", "production", "daemon", DEAD_PORT)
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

  writeFileSync(join(WORK, "state.json"), JSON.stringify({
    baseUrl: BASE,
    succeeded,
    pids: children.map((c) => c.pid).filter((p): p is number => typeof p === "number"),
    hubGroup: hub.pid
  }, null, 2));
}

export { WORK };
