import { chromium, devices, type Browser, type FullConfig } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import path from "node:path";
import { hostSessionPath, presenterSessionPath } from "./support/session";

/** Reads a passcode from the environment; never logged or written anywhere but the sign-in form. */
const passcodeFrom = (variable: string, setting: string, ciValue: string) => {
  const passcode = process.env[variable] ?? "";
  if (passcode === "") {
    throw new Error(`Set ${variable} to the API's ${setting} (CI: ${ciValue}).`);
  }
  return passcode;
};

/**
 * Signs the host and the presenter in once each through the /demo-login form and saves both sessions (cookies) for
 * every spec file (Design §18.5, §18.7). /demo-login allows 5 attempts a minute per IP, so two sign-ins per run leave
 * room for the wrong-passcode test. Uses the first project's browser channel and base URL.
 */
export default async function globalSetup(config: FullConfig) {
  const hostPasscode = passcodeFrom("E2E_HOST_PASSCODE", "Demo:HostPasscode", "ci-host-passcode");
  const presenterPasscode = passcodeFrom(
    "E2E_PRESENTER_PASSCODE",
    "Demo:PresenterPasscode",
    "ci-presenter-passcode",
  );

  const { baseURL, channel } = config.projects[0].use;
  const browser = await chromium.launch({ channel });
  try {
    await signIn(browser, baseURL, hostPasscode, /\/host\/$/, "floor-plan", hostSessionPath);
    await signIn(browser, baseURL, presenterPasscode, /\/presenter\/$/, "phone-panel", presenterSessionPath);
  } finally {
    await browser.close();
  }
}

/** One sign-in in a fresh context: the form, the role's screen, then the session saved to `sessionPath`. */
const signIn = async (
  browser: Browser,
  baseURL: string | undefined,
  passcode: string,
  landsOn: RegExp,
  screenTestId: string,
  sessionPath: string,
) => {
  const context = await browser.newContext({
    ...devices["Galaxy Tab S4 landscape"],
    baseURL,
    ignoreHTTPSErrors: true, // local dev certificates
  });
  try {
    const page = await context.newPage();
    await page.goto("/demo-login/");
    await page.getByLabel("Passcode", { exact: true }).fill(passcode);
    await page.getByRole("button", { name: "Sign in" }).click();
    await page.waitForURL(landsOn);
    await page.getByTestId(screenTestId).waitFor();
    await mkdir(path.dirname(sessionPath), { recursive: true });
    await context.storageState({ path: sessionPath });
  } finally {
    await context.close();
  }
};
