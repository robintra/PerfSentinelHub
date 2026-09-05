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

// Every still fast-forwards finite CSS transitions. The fold chevron rotates
// from > to v over 150 ms, and a panel that fills faster than that lands the
// shot mid-rotation, where the two arms read as a check mark rather than a
// chevron.

// The launcher renders into #main after it has fetched, so a screenshot taken
// on load catches an empty shell.
async function settled(page: Page, marker: string | RegExp): Promise<void> {
  await expect(page.locator("#main")).toContainText(marker, { timeout: 15_000 });
  // A sticky footer lands wherever the viewport sat, so a full-page capture
  // bakes it into the middle of the page with content below it.
  await page.addStyleTag({ content: ".shell-footer { position: static; }" });
  await expect
    .poll(() => page.locator(".shell-footer")
      .evaluate((node) => getComputedStyle(node).position))
    .toBe("static");
}

test("run an analysis", async ({ page }, info) => {
  await page.goto("/#/new");
  await settled(page, "Run an analysis");
  // The trace-backend form is the full one: service, window, trace cap. A
  // daemon takes no parameters at all, so it would show an empty form.
  await page.locator("button.source-row", { hasText: "Tempo EU" }).click();
  // The service field exists only for a trace backend, so its arrival is the
  // signal that the form has swapped.
  await expect(page.locator('input[placeholder="order-service"]')).toBeVisible();
  await page.screenshot({ path: nameFor("launcher-new", info.project.name), fullPage: true, animations: "disabled" });
});

test("recent runs", async ({ page }, info) => {
  await page.goto("/#/recent");
  await settled(page, "The team's short memory");
  await page.screenshot({ path: nameFor("launcher-recent", info.project.name), fullPage: true, animations: "disabled" });
});

test("fleet health", async ({ page }, info) => {
  await page.goto("/#/sources");
  await settled(page, "Fleet health");
  // A folded row shows none of the gauges, which are the point of the screen.
  await page.locator("button.row-toggle", { hasText: "Checkout production" }).click();
  // The gauges are what the daemon read produces, and they are the point of the
  // screen, so waiting for the first one waits for the whole panel.
  await expect(page.locator(".daemon-panel .count").first()).toBeVisible({ timeout: 15_000 });
  await page.screenshot({ path: nameFor("launcher-sources", info.project.name), fullPage: true, animations: "disabled" });
});

test("incidents", async ({ page }, info) => {
  await page.goto("/#/incidents");
  await settled(page, "What was already burning");
  // A folded row shows none of the findings the daemon froze, which are the
  // point of the screen.
  await page.locator("button.row-toggle").first().click();
  await expect(page.locator(".daemon-panel .table").first()).toBeVisible({ timeout: 15_000 });
  await page.screenshot({ path: nameFor("launcher-incidents", info.project.name), fullPage: true, animations: "disabled" });
});

test("the window handed to a new analysis", async ({ page }, info) => {
  await page.goto("/#/incidents");
  await settled(page, "What was already burning");
  // Named on purpose: this row carries both a kind the banner can say and a
  // namespace, which is the pair the sentence is built for.
  await page.locator("button.row-toggle", { hasText: "checkout-svc" }).click();
  await expect(page.locator(".daemon-panel .table").first()).toBeVisible({ timeout: 15_000 });
  await page.getByRole("button", { name: "Analyse this window" }).first().click();
  await expect(page).toHaveURL(/#\/new\?from=\d+&to=\d+&service=[^&]+&incident=[0-9a-f]{32}$/);
  await expect(page.locator(".banner", { hasText: "from the incidents screen" })).toBeVisible();
  // A daemon takes no window, so the shot is the form that can run it. The
  // service the link carried has to survive the source switch, which is the
  // half of the prefill a still cannot show on its own.
  await page.locator("button.source-row", { hasText: "Tempo EU" }).click();
  await expect(page.locator('input[placeholder="order-service"]')).toHaveValue(/\S/);
  await page.screenshot({ path: nameFor("launcher-handoff", info.project.name), fullPage: true, animations: "disabled" });
});

test("one run", async ({ page }, info) => {
  await page.goto(`/#/run/${state.succeeded}`);
  await settled(page, /succeeded/i);
  await page.screenshot({ path: nameFor("launcher-run", info.project.name), fullPage: true, animations: "disabled" });
});

test("the rendered report", async ({ page }, info) => {
  await page.goto(`/#/report/${state.succeeded}`);
  // The dashboard is an iframe on the same origin. Waiting on its content
  // rather than on a delay, because a blank frame screenshots just as well.
  await expect(page.frameLocator("iframe").locator("body"))
    .toContainText("Findings", { timeout: 30_000 });
  await page.screenshot({ path: nameFor("launcher-report", info.project.name), animations: "disabled" });
});
