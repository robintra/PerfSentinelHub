import { test, expect, Locator, Page } from "@playwright/test";

// The walkthrough the GIFs show. It is paced for watching, not for speed: every
// step lingers long enough to read what it just revealed, because a reader
// cannot pause a GIF.
//
// Three acts: shaping a run against a trace backend, running one against a
// daemon, then reading the fleet. The folds are the point of several screens,
// so the tour opens them rather than describing them.

const beat = (page: Page, ms: number) => page.waitForTimeout(ms);

/** Opens a fold and holds on what it revealed, scrolling it into frame first. */
async function unfold(page: Page, control: Locator, hold: number): Promise<void> {
  if (!(await control.count())) return;
  await control.scrollIntoViewIfNeeded();
  await beat(page, 700);
  await control.click();
  await beat(page, 600);
  await control.scrollIntoViewIfNeeded();
  await beat(page, hold);
}

test("launcher tour", async ({ page }) => {
  // --- shaping a run against a trace backend ---
  await page.goto("/#/new");
  await expect(page.locator("#main")).toContainText("Run an analysis", { timeout: 15_000 });
  await beat(page, 3200);

  // A backend takes the full form: service, window, trace cap. A daemon takes
  // none, so this is the half of the screen worth showing first.
  await page.locator("button.source-row", { hasText: "Tempo EU" }).click();
  await beat(page, 3000);

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

  await page.mouse.wheel(0, -900);
  await beat(page, 800);
  await page.locator("button.source-row", { hasText: "Checkout production" }).click();
  await beat(page, 3200);

  // --- running it ---
  await page.locator("button.submit").click();
  await expect(page).toHaveURL(/#\/run\/[0-9a-f]{16}/, { timeout: 60_000 });
  await expect(page.locator("#main")).toContainText(/succeeded/i, { timeout: 60_000 });
  await beat(page, 4800);

  const open = page.locator("a", { hasText: "Open the dashboard" }).first();
  if (await open.count()) {
    await open.click();
    await beat(page, 6000);
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
  await unfold(page, page.locator("button.settings-card-head").first(), 3200);
  await unfold(page, page.locator("button.terminal-more").first(), 3600);
  await beat(page, 1500);
});
