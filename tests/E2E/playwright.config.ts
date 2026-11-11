import { defineConfig, devices } from "@playwright/test";

/**
 * The API and the web app must already be running (Design §18.4):
 * - locally: `dotnet run --project src/Api` and `cd web && npm run dev` → https://localhost:3000 (self-signed);
 * - in CI: the API on http://localhost:5000 and the static export served on http://localhost:3000 (E2E_BASE_URL).
 */
const isCi = !!process.env.CI;

export default defineConfig({
  testDir: "./specs",
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
  },
  // Mobile first (AC1.7): a phone-sized Chromium. CI uses Playwright's Linux Chromium; locally the installed
  // Microsoft Edge (same engine), because Chrome crashes under automation on the development laptop.
  projects: isCi
    ? [{ name: "mobile-chromium", use: { ...devices["Pixel 7"] } }]
    : [{ name: "mobile-edge", use: { ...devices["Pixel 7"], channel: "msedge" } }],
});
