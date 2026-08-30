import { test, expect, Page } from "@playwright/test";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";

// One still per screen, in both themes. The light file keeps the bare name
// because it fills the <img src> slot of a <picture>; the dark one takes the
// -dark suffix the engine's docs already use.

const OUT = join(__dirname, "..", "..", "..", "docs", "img", "hub");
// Written by global-setup once the Hub is up and the runs have settled. Read at
// module scope, so a setup that died earlier would otherwise fail every test
// here with a bare ENOENT naming a temp path and nothing else.
const STATE_FILE = join(tmpdir(), "perf-sentinel-hub-demo", "state.json");
let state: { succeeded: string };
try {
  state = JSON.parse(readFileSync(STATE_FILE, "utf8")) as { succeeded: string };
} catch (error) {
  throw new Error(
    `No demo state at ${STATE_FILE}. global-setup did not finish: it stands up the fake ` +
    `daemons, the Hub and the analysis runs these stills need. Its own error is above. ` +
    `(${String(error)})`);
}

const nameFor = (screen: string, project: string) =>
  join(OUT, project.endsWith("-dark") ? `${screen}-dark.png` : `${screen}.png`);

// The launcher renders into #main after it has fetched, so a screenshot taken
// on load catches an empty shell.
async function settled(page: Page, marker: string | RegExp): Promise<void> {
  await expect(page.locator("#main")).toContainText(marker, { timeout: 15_000 });
  // A sticky footer lands wherever the viewport sat, so a full-page capture
  // bakes it into the middle of the page with content below it.
  await page.addStyleTag({ content: ".shell-footer { position: static; }" });
  await page.waitForTimeout(400);
}

test("run an analysis", async ({ page }, info) => {
  await page.goto("/#/new");
  await settled(page, "Run an analysis");
  // The trace-backend form is the full one: service, window, trace cap. A
  // daemon takes no parameters at all, so it would show an empty form.
  await page.locator("button.source-row", { hasText: "Tempo EU" }).click();
  await page.waitForTimeout(700);
  await page.screenshot({ path: nameFor("launcher-new", info.project.name), fullPage: true });
});

test("recent runs", async ({ page }, info) => {
  await page.goto("/#/recent");
  await settled(page, "The team's short memory");
  await page.screenshot({ path: nameFor("launcher-recent", info.project.name), fullPage: true });
});

test("fleet health", async ({ page }, info) => {
  await page.goto("/#/sources");
  await settled(page, "Fleet health");
  // A folded row shows none of the gauges, which are the point of the screen.
  await page.locator("button.row-toggle", { hasText: "Checkout production" }).click();
  await page.waitForTimeout(2000);
  await page.screenshot({ path: nameFor("launcher-sources", info.project.name), fullPage: true });
});

test("one run", async ({ page }, info) => {
  await page.goto(`/#/run/${state.succeeded}`);
  await settled(page, /succeeded/i);
  await page.screenshot({ path: nameFor("launcher-run", info.project.name), fullPage: true });
});

test("the rendered report", async ({ page }, info) => {
  await page.goto(`/#/report/${state.succeeded}`);
  await page.waitForTimeout(4000);
  await page.screenshot({ path: nameFor("launcher-report", info.project.name) });
});
