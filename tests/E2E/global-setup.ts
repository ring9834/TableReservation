import { chromium, devices, type FullConfig } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import path from "node:path";
import { hostSessionPath } from "./support/session";

/**
 * Signs the host in once through the /demo-login form and saves the session (cookie) for every spec file
 * (Design §18.5). /demo-login allows 5 attempts a minute per IP, so one sign-in per run leaves room for the
 * wrong-passcode test. Uses the first project's browser channel and base URL.
 */
export default async function globalSetup(config: FullConfig) {
  const passcode = process.env.E2E_HOST_PASSCODE ?? "";
  if (passcode === "") {
    throw new Error("Set E2E_HOST_PASSCODE to the API's Demo:HostPasscode (CI: ci-host-passcode).");
  }

  const { baseURL, channel } = config.projects[0].use;
  const browser = await chromium.launch({ channel });
  try {
    const context = await browser.newContext({
      ...devices["Galaxy Tab S4 landscape"],
      baseURL,
      ignoreHTTPSErrors: true, // local dev certificates
    });
    const page = await context.newPage();
    await page.goto("/demo-login/");
    await page.getByLabel("Passcode", { exact: true }).fill(passcode);
    await page.getByRole("button", { name: "Sign in" }).click();
    await page.waitForURL(/\/host\/$/);
    await page.getByTestId("floor-plan").waitFor();
    await mkdir(path.dirname(hostSessionPath), { recursive: true });
    await context.storageState({ path: hostSessionPath });
  } finally {
    await browser.close();
  }
}
