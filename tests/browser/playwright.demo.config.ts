import { defineConfig, devices } from "@playwright/test";

// Demo-only config. `npm run demo` ships two artefact groups to docs/img/hub/:
//   - launcher_dark.gif, launcher_light.gif      (tour.spec.ts)
//   - <screen>.png and <screen>-dark.png         (stills.spec.ts)
// The light stills keep the bare name because they fill the <img src> slot of
// a <picture>, which is what a reader with no dark preference gets.
//
// The Hub has no seeder and reads its daemons live, so global-setup.ts stands
// up two fake daemons and a real Hub before any of this runs.

// 1280x900 holds the whole fleet screen with a daemon row unfolded.
const VIEWPORT = { width: 1280, height: 900 } as const;

export default defineConfig({
  testDir: "./demo",
  fullyParallel: false,
  retries: 0,
  workers: 1,
  outputDir: "./demo-videos",
  // The tour records a walkthrough rather than asserts on one, and it is paced
  // for someone watching a GIF they cannot pause. Playwright's 30 s default is
  // shorter than the tour itself.
  timeout: 240_000,
  reporter: [["list"]],
  globalSetup: "./global-setup.ts",
  globalTeardown: "./global-teardown.ts",
  use: {
    baseURL: process.env.HUB_BASE_URL || "http://127.0.0.1:41500",
    trace: "off",
    screenshot: "off",
    viewport: VIEWPORT
  },
  projects: [
    {
      name: "launcher-dark",
      testMatch: "tour.spec.ts",
      use: { ...devices["Desktop Chrome"], viewport: VIEWPORT, colorScheme: "dark",
             video: { mode: "on", size: VIEWPORT } }
    },
    {
      name: "launcher-light",
      testMatch: "tour.spec.ts",
      use: { ...devices["Desktop Chrome"], viewport: VIEWPORT, colorScheme: "light",
             video: { mode: "on", size: VIEWPORT } }
    },
    {
      name: "launcher-stills-dark",
      testMatch: "stills.spec.ts",
      use: { ...devices["Desktop Chrome"], viewport: VIEWPORT, colorScheme: "dark", video: "off" }
    },
    {
      name: "launcher-stills-light",
      testMatch: "stills.spec.ts",
      use: { ...devices["Desktop Chrome"], viewport: VIEWPORT, colorScheme: "light", video: "off" }
    }
  ]
});
