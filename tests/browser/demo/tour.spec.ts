import { test, expect, Locator, Page } from "@playwright/test";

// The walkthrough the GIFs show. It is paced for watching, not for speed: every
// step lingers long enough to read what it just revealed, because a reader
// cannot pause a GIF.
//
// Four acts: shaping a run against a trace backend, running one against a
// daemon, reading the fleet, then reading what was already burning when the
// alerting posted an incident and handing that window back to a run. The folds
// are the point of several screens, so the tour opens them rather than
// describing them.

const beat = (page: Page, ms: number) => page.waitForTimeout(ms);

/** Holds on something, bringing it into frame first. A bare wait dwells on
 * whatever the viewport happens to show, which on a page taller than the
 * window is often not the thing the step means to present. */
async function hold(page: Page, target: Locator, ms: number): Promise<void> {
  if (await target.count()) {
    await target.first().scrollIntoViewIfNeeded();
    await beat(page, 500);
  }
  await beat(page, ms);
}

/** Opens a fold and holds on what it revealed, scrolling it into frame first. */
async function unfold(page: Page, control: Locator, ms: number): Promise<void> {
  if (!(await control.count())) return;
  await control.first().scrollIntoViewIfNeeded();
  await beat(page, 700);
  await control.first().click();
  await beat(page, 600);
  await control.first().scrollIntoViewIfNeeded();
  await beat(page, ms);
}

test("launcher tour", async ({ page }) => {
  // --- shaping a run against a trace backend ---
  await page.goto("/#/new");
  await expect(page.locator("#main")).toContainText("Run an analysis", { timeout: 15_000 });
  await beat(page, 3200);

  // A backend takes the full form: service, window, trace cap. A daemon takes
  // none, so this is the half of the screen worth showing first.
  await page.locator("button.source-row", { hasText: "Tempo EU" }).click();
  await hold(page, page.locator(".card").first(), 3000);

  await page.locator('input[placeholder="order-service"]').fill("");
  await page.locator('input[placeholder="order-service"]').type("checkout-api", { delay: 90 });
  await beat(page, 2200);

  const range = page.locator("button.range-pill");
  if (await range.count()) {
    await range.click();
    await beat(page, 2800);
    await page.keyboard.press("Escape");
    await beat(page, 1200);
  }

  await unfold(page, page.locator("button.sink-more"), 3200);
  // The printed command is the screen's own answer to "what would I type
  // instead", so it gets the longest hold on this act.
  await unfold(page, page.locator("button.terminal-more"), 4500);

  // The one place the launcher exposes the engine's own thresholds. A backend
  // source is what makes it appear, so it has to be opened before the daemon is
  // selected below.
  await unfold(page, page.locator("summary.advanced-summary"), 4000);
  await hold(page, page.locator("input.input-knob").first(), 2500);

  await page.mouse.wheel(0, -900);
  await beat(page, 800);
  await page.locator("button.source-row", { hasText: "Checkout production" }).click();
  await beat(page, 3200);

  // --- running it ---
  await page.locator("button.submit").click();
  await expect(page).toHaveURL(/#\/run\/[0-9a-f]{16}/, { timeout: 60_000 });
  await expect(page.locator("#main")).toContainText(/succeeded/i, { timeout: 60_000 });
  await hold(page, page.locator(".outcome"), 4800);

  const open = page.locator("a", { hasText: "Open the dashboard" }).first();
  if (await open.count()) {
    await open.click();
    await hold(page, page.locator(".report-shell"), 6000);
  }

  // --- reading the fleet ---
  await page.goto("/#/recent");
  await expect(page.locator("#main")).toContainText("The team's short memory", { timeout: 15_000 });
  await beat(page, 4200);

  await page.goto("/#/sources");
  await expect(page.locator("#main")).toContainText("Fleet health", { timeout: 15_000 });
  await beat(page, 2600);

  await unfold(page, page.locator("button.row-toggle", { hasText: "Checkout production" }), 4800);
  await unfold(page, page.locator("button.settings-more"), 3400);
  await unfold(page, page.locator("button.settings-card-head").nth(0), 3200);
  await unfold(page, page.locator("button.settings-card-head").nth(1), 3000);
  await unfold(page, page.locator("button.terminal-more").first(), 3600);
  await beat(page, 1500);

  // --- what was already burning ---
  await page.goto("/#/incidents");
  await expect(page.locator("#main")).toContainText("What was already burning", { timeout: 15_000 });
  await beat(page, 3000);

  // The kind select narrows a fleet that flapped all night to the one event
  // worth opening, and the row it leaves is the one the tour then opens.
  const kind = page.locator("select.refresh-select").first();
  if (await kind.count()) {
    await kind.selectOption("oom_kill");
    await beat(page, 3000);
  }

  await unfold(page, page.locator("button.row-toggle", { hasText: "checkout-svc" }), 4000);

  // The findings are frozen, so the last step is the one that reads them
  // against live traces: the same window, handed to a run that can take it.
  const analyse = page.getByRole("button", { name: "Analyse this window" }).first();
  if (await analyse.count()) {
    await analyse.scrollIntoViewIfNeeded();
    await beat(page, 900);
    await analyse.click();
    await expect(page.locator(".banner")).toContainText("from the incidents screen", { timeout: 15_000 });
    // The source that arrives selected is the daemon of act two, which takes no
    // window, so the banner asks for a backend. Answering it is the last beat:
    // the window and the service survive the switch, and the run can go.
    await hold(page, page.locator(".banner").first(), 3600);
    await page.locator("button.source-row", { hasText: "Tempo EU" }).click();
    await expect(page.locator('input[placeholder="order-service"]')).toHaveValue(/\S/);
    await hold(page, page.locator("button.range-pill").first(), 4200);
  }
});
