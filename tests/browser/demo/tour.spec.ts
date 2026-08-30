import { test, expect } from "@playwright/test";

// The path a first-time reader takes: pick a source, submit, watch the run
// land, open the dashboard it produced, then look at the fleet. Recorded as
// video and turned into a GIF by build-gif.sh.

test("launcher tour", async ({ page }) => {
  await page.goto("/#/new");
  await expect(page.locator("#main")).toContainText("Run an analysis", { timeout: 15_000 });
  await page.waitForTimeout(2000);

  await page.locator("button.source-row", { hasText: "Checkout production" }).click();
  await page.waitForTimeout(1500);
  await page.locator("button.submit").click();

  // A daemon run reads what the daemon already found, so it lands fast.
  await expect(page).toHaveURL(/#\/run\/[0-9a-f]{16}/, { timeout: 60_000 });
  // Upper case on screen is a CSS transform, the DOM text is lower case.
  await expect(page.locator("#main")).toContainText(/succeeded/i, { timeout: 60_000 });
  await page.waitForTimeout(3000);

  const open = page.locator("a", { hasText: "Open the dashboard" }).first();
  if (await open.count()) {
    await open.click();
    await page.waitForTimeout(5000);
  }

  await page.goto("/#/sources");
  await expect(page.locator("#main")).toContainText("Fleet health", { timeout: 15_000 });
  await page.locator("button.row-toggle", { hasText: "Checkout production" }).click();
  await page.waitForTimeout(3500);
});
