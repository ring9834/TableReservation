import { defineConfig, devices } from "@playwright/test";
import { recording, slowMoMs } from "./support/recording";

/**
 * The API and the web app must already be running (Design §18.4):
 * - locally: `dotnet run --project src/Api` and `cd web && npm run dev` → https://localhost:3000 (self-signed);
 * - in CI: the API on http://localhost:5000 and the static export served on http://localhost:3000 (E2E_BASE_URL).
 * global-setup.ts signs the host in once (E2E_HOST_PASSCODE) and saves the session for every spec file (Design §18.5).
 */
const isCi = !!process.env.CI;

/**
 * The devices the screen helpers open (support/screens.ts reads them from the project's metadata): the guest's phone
 * and the host's tablet. The presenter is always a laptop.
 */
export type ScreenDevices = { phone: string; tablet: string };

const androidAndGalaxyTab: ScreenDevices = { phone: "Pixel 7", tablet: "Galaxy Tab S4 landscape" };
const iPhoneAndIPad: ScreenDevices = { phone: "iPhone 15", tablet: "iPad Pro 11 landscape" };

/** QA-8 (M11b, Design §17.3): the guest specs on an iPhone, the host specs on an iPad, both in WebKit. */
const guestSpecs = [/booking\.spec\.ts/, /deposit\.spec\.ts/, /manage\.spec\.ts/, /phone-layout\.spec\.ts/];
const hostSpecs = [/host\.spec\.ts/, /walk-in\.spec\.ts/, /floor-plan\.spec\.ts/];

/**
 * The WebKit projects' test budget (M11c, Design §18.11). Playwright's WebKit on Windows is slow to drive: in the M11c
 * traces every driver round trip (locate, actionability, click, fill) took 0.4-1.5 s, so a multi-screen walk-in test
 * spent 4-6 s per click and ran out of 30 s with every request answered and every check passing. Only the test's
 * total budget is larger; the app-speed checks (2 s screen updates, 1 s host actions, expect timeouts) are the same
 * everywhere, and the default project keeps 30 s.
 */
const webkitTestTimeoutMs = 60_000;

export default defineConfig({
  testDir: "./specs",
  globalSetup: "./global-setup.ts",
  fullyParallel: false, // the specs book real tables in one shared database
  workers: 1,
  forbidOnly: isCi,
  retries: isCi ? 1 : 0,
  reporter: isCi ? [["list"], ["html", { open: "never" }]] : [["list"]],
  timeout: 30_000,
  use: {
    baseURL: process.env.E2E_BASE_URL ?? "https://localhost:3000",
    ignoreHTTPSErrors: true, // local dev certificates (next dev --experimental-https, dotnet dev-certs)
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    // DEMO_RECORD=1: a watchable pace for the fallback recording of the demo script (M11b).
    launchOptions: recording ? { slowMo: slowMoMs } : undefined,
  },
  // Mobile first (AC1.7): a phone-sized Chromium. CI uses Playwright's Linux Chromium; locally the installed
  // Microsoft Edge (same engine), because Chrome crashes under automation on the development laptop. The first
  // project's channel is the one global-setup.ts signs in with.
  // QA-8 locally adds WebKit (`npx playwright install webkit` once): an iPhone for the guest specs and an iPad for the
  // host specs. Not in CI yet (download size and time).
  projects: isCi
    ? [{ name: "mobile-chromium", use: { ...devices["Pixel 7"] }, metadata: androidAndGalaxyTab }]
    : [
        { name: "mobile-edge", use: { ...devices["Pixel 7"], channel: "msedge" }, metadata: androidAndGalaxyTab },
        {
          name: "iphone-webkit",
          use: { ...devices["iPhone 15"] },
          testMatch: guestSpecs,
          metadata: iPhoneAndIPad,
          timeout: webkitTestTimeoutMs,
        },
        {
          name: "tablet-webkit",
          use: { ...devices["iPad Pro 11 landscape"] },
          testMatch: hostSpecs,
          metadata: iPhoneAndIPad,
          timeout: webkitTestTimeoutMs,
        },
      ],
});
