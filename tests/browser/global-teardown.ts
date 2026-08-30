import { readFileSync, rmSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";

const WORK = join(tmpdir(), "perf-sentinel-hub-demo");

export default async function globalTeardown(): Promise<void> {
  try {
    const state = JSON.parse(readFileSync(join(WORK, "state.json"), "utf8")) as
      { pids: number[]; hubGroup?: number };
    // The Hub is a grandchild of `dotnet run`, so the group gets the signal.
    if (state.hubGroup) {
      try { process.kill(-state.hubGroup, "SIGTERM"); } catch { /* already gone */ }
    }
    for (const pid of state.pids) {
      // Every child is detached, so each one leads its own group. Signalling the
      // group reaches anything it spawned; the bare pid is the fallback for a
      // process that already reaped its group.
      try { process.kill(-pid, "SIGTERM"); } catch { /* not a group leader */ }
      try { process.kill(pid, "SIGTERM"); } catch { /* already gone */ }
    }
  } catch {
    // Setup failed before it wrote the file, so there is nothing to stop.
  }
  rmSync(WORK, { recursive: true, force: true });
}
