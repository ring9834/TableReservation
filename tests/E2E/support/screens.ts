import { devices, expect, type Browser, type BrowserContext, type BrowserContextOptions, type Page } from "@playwright/test";
import { hostSessionPath, presenterSessionPath } from "./session";

const baseOptions = (baseURL: string | undefined): BrowserContextOptions => ({
  baseURL,
  ignoreHTTPSErrors: true, // local dev certificates
});

/** A host tablet, signed in with the shared session (global-setup.ts). */
export const hostContext = (browser: Browser, baseURL: string | undefined) =>
  browser.newContext({ ...devices["Galaxy Tab S4 landscape"], ...baseOptions(baseURL), storageState: hostSessionPath });

/** The presenter's laptop (or projector), signed in with the shared presenter session (global-setup.ts). */
export const presenterContext = (browser: Browser, baseURL: string | undefined) =>
  browser.newContext({
    ...devices["Desktop Chrome"],
    viewport: { width: 1366, height: 768 },
    ...baseOptions(baseURL),
    storageState: presenterSessionPath,
  });

/** Opens /presenter/ and waits until the phone panel has loaded today's messages (Design §18.7). */
export const openPresenterScreen = async (context: BrowserContext) => {
  const page = await context.newPage();
  await page.goto("/presenter/");
  await expect(page.getByTestId("phone-panel").getByRole("log")).toBeVisible();
  await expect(page.getByTestId("phone-panel")).not.toContainText(/Loading|加载/);
  return page;
};

/** The phone panel's messages that contain `text` (a booking reference, a few template words). */
export const phoneMessages = (page: Page, text?: string | RegExp) => {
  const messages = page.getByTestId("phone-message");
  return text === undefined ? messages : messages.filter({ hasText: text });
};

/** A guest phone. */
export const guestContext = (browser: Browser, baseURL: string | undefined) =>
  browser.newContext({ ...devices["Pixel 7"], ...baseOptions(baseURL) });

/** A fictional guest name; the "E2E " prefix lets a later run find what an aborted run left behind. */
export const uniqueGuestName = () => `E2E ${Math.random().toString(36).slice(2, 7).toUpperCase()}`;

/** A fictional mobile (021 0xx xxxx) per guest: a known mobile reuses the stored guest and its name (Design §14.1). */
export const uniqueMobile = () => {
  const digits = Math.floor(Math.random() * 1_000_000)
    .toString()
    .padStart(6, "0");
  return `021 0${digits.slice(0, 2)} ${digits.slice(2)}`;
};
