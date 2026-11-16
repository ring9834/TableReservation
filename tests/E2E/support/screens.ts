import {
  devices,
  expect,
  test,
  type Browser,
  type BrowserContext,
  type BrowserContextOptions,
  type Page,
} from "@playwright/test";
import { hostSessionPath, presenterSessionPath } from "./session";

const baseOptions = (baseURL: string | undefined): BrowserContextOptions => ({
  baseURL,
  ignoreHTTPSErrors: true, // local dev certificates
});

type ScreenDevices = { phone: string; tablet: string };

const androidAndGalaxyTab: ScreenDevices = { phone: "Pixel 7", tablet: "Galaxy Tab S4 landscape" };

/**
 * The guest's phone and the host's tablet for the running project (playwright.config.ts metadata, QA-8): a Pixel 7
 * and a Galaxy Tab in Edge / Chromium, an iPhone and an iPad in WebKit. Outside a test, the Android pair.
 */
export const screenDevices = (): ScreenDevices => {
  try {
    const metadata = test.info().project.metadata as Partial<ScreenDevices>;
    return { ...androidAndGalaxyTab, ...metadata };
  } catch {
    return androidAndGalaxyTab;
  }
};

/** The project's phone as context options (viewport, user agent, touch). */
export const phoneDevice = () => devices[screenDevices().phone];

/** A host tablet, signed in with the shared session (global-setup.ts). Extra options add to it (a video, M11b). */
export const hostContext = (browser: Browser, baseURL: string | undefined, extra: BrowserContextOptions = {}) =>
  browser.newContext({
    ...devices[screenDevices().tablet],
    ...baseOptions(baseURL),
    storageState: hostSessionPath,
    ...extra,
  });

/** The presenter's laptop or projector. */
export const presenterViewport = { width: 1366, height: 768 };

/** The presenter's laptop (or projector), signed in with the shared presenter session (global-setup.ts). */
export const presenterContext = (browser: Browser, baseURL: string | undefined, extra: BrowserContextOptions = {}) =>
  browser.newContext({
    ...devices["Desktop Chrome"],
    viewport: presenterViewport,
    ...baseOptions(baseURL),
    storageState: presenterSessionPath,
    ...extra,
  });

/** Opens /presenter/ and waits until the phone panel and the POS panel have loaded (Design §18.7, §18.9). */
export const openPresenterScreen = async (context: BrowserContext) =>
  loadPresenterScreen(await context.newPage());

/** Loads /presenter/ in `page` (one the caller is already watching) and waits as `openPresenterScreen` does. */
export const loadPresenterScreen = async (page: Page) => {
  await page.goto("/presenter/");
  await expect(page.getByTestId("phone-panel").getByRole("log")).toBeVisible();
  await expect(page.getByTestId("phone-panel")).not.toContainText(/Loading|加载/);
  await expect(page.getByTestId("pos-panel")).not.toContainText(/Loading|加载/);
  await expect(page.getByTestId("pos-switch")).toBeEnabled();
  return page;
};

/** The POS log's rows on the presenter screen, optionally one message type and booking (Design §18.9). */
export const posRows = (page: Page, filter: { type?: string; bookingId?: string } = {}) =>
  page.locator(
    '[data-testid="pos-message"]' +
      (filter.type ? `[data-type="${filter.type}"]` : "") +
      (filter.bookingId ? `[data-booking-id="${filter.bookingId}"]` : ""),
  );

/** The phone panel's messages that contain `text` (a booking reference, a few template words). */
export const phoneMessages = (page: Page, text?: string | RegExp) => {
  const messages = page.getByTestId("phone-message");
  return text === undefined ? messages : messages.filter({ hasText: text });
};

/** A guest phone: the project's phone (a Pixel 7 by default, an iPhone in WebKit). */
export const guestContext = (browser: Browser, baseURL: string | undefined, extra: BrowserContextOptions = {}) =>
  browser.newContext({ ...phoneDevice(), ...baseOptions(baseURL), ...extra });

/** A fictional guest name; the "E2E " prefix lets a later run find what an aborted run left behind. */
export const uniqueGuestName = () => `E2E ${Math.random().toString(36).slice(2, 7).toUpperCase()}`;

/** A fictional mobile (021 0xx xxxx) per guest: a known mobile reuses the stored guest and its name (Design §14.1). */
export const uniqueMobile = () => {
  const digits = Math.floor(Math.random() * 1_000_000)
    .toString()
    .padStart(6, "0");
  return `021 0${digits.slice(0, 2)} ${digits.slice(2)}`;
};
